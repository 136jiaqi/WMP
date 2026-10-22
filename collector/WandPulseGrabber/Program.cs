using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// === Exit codes ===
// 0 = success, 2 = SPA timeout, 3 = Pulse not rendered, 4 = extract failed, 5 = no rows, 6 = login

if (args.Length < 1)
{
    Console.WriteLine("Usage: WandPulseGrabber <output_csv_path>");
    return 1;
}

var csvPath = args[0];

// ============ Step 1: Connect to Wand via CDP ============
using var http = new HttpClient();
var pagesResp = await http.GetAsync("http://127.0.0.1:9222/json");
if (!pagesResp.IsSuccessStatusCode)
{
    Console.WriteLine("Wand is not running or DevTools port 9222 is not exposed...");
    return 19;
}

var pagesJson = await pagesResp.Content.ReadAsStringAsync();
using var pagesDoc = JsonDocument.Parse(pagesJson);
string wsUrl = "";
foreach (var page in pagesDoc.RootElement.EnumerateArray())
{
    if (page.TryGetProperty("type", out var type) && type.GetString() == "page"
        && page.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
    {
        wsUrl = wsProp.GetString() ?? "";
        break;
    }
}
if (string.IsNullOrEmpty(wsUrl))
{
    Console.WriteLine("No page target found in CDP response.");
    return 20;
}

Console.WriteLine($"Connecting to Wand CDP: {wsUrl}");
using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(wsUrl), new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);
Console.WriteLine("Connected!");

// ============ Helpers ============
async Task<string> SendCommandAsync(string method, JsonElement? @params = null)
{
    var id = Guid.NewGuid().GetHashCode() & 0x7FFFFFFF;
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { id, method, @params }));
    var msg = doc.RootElement.GetRawText();
    var bytes = Encoding.UTF8.GetBytes(msg);
    await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);

    var buffer = new byte[16384];
    var ms = new MemoryStream();
    while (true)
    {
        var seg = new ArraySegment<byte>(buffer);
        var result = await ws.ReceiveAsync(seg, new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);
        if (result.MessageType == WebSocketMessageType.Close) break;
        ms.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            if (reply.RootElement.TryGetProperty("id", out var replyId) && replyId.GetInt32() == id) break;
            ms.SetLength(0); // Ignore CDP events and replies to other commands.
        }
    }
    var resp = Encoding.UTF8.GetString(ms.ToArray());
    using var rdoc = JsonDocument.Parse(resp);
    if (rdoc.RootElement.TryGetProperty("result", out var res)
        && res.TryGetProperty("result", out var inner)
        && inner.TryGetProperty("value", out var val))
    {
        return val.GetString() ?? "";
    }
    return "";
}

async Task<string> EvalAsync(string expr)
{
    var paramJson = JsonSerializer.Serialize(new { expression = expr, returnByValue = true });
    using var pdoc = JsonDocument.Parse(paramJson);
    return await SendCommandAsync("Runtime.evaluate", pdoc.RootElement);
}

// ============ Step 2: Wait for SPA shell ============
// Prior builds required no loading/spinner/skeleton element to be present,
// but Wand's dashboard page keeps a loading-class element around persistently
// (e.g. a hidden spinner shell). Checking offsetParent !== null is stricter
// than needed either — the element may have display:none but still carry the
// class name. Instead we require the sidebar AND the main content area to be
// present (meaning the initial HTML shell has mounted), and we look for the
// "home" nav text that always renders when SPA truly settles.
const string spaReadyJs = @"
(function(){
    var sidebar = document.querySelector('.app-sidebar, [class*=sidebar], [class*=Sidebar]');
    var mainContent = document.querySelector('.app-main, [class*=main-content], [class*=MainContent], main, [class*=content]');
    var loginRequired = false;
    var body = (document.body ? document.body.textContent : '') || '';
    if (body.indexOf('立即登录') >= 0 || body.indexOf('进入 Wand') >= 0 || body.indexOf('使用 Google') >= 0) {
        loginRequired = true;
    }
    // Looser ready: sidebar present AND (main content OR 'home' nav text visible)
    // Don't require no [class*=loading] element — Wand keeps one around even when idle.
    var hasHomeText = body.indexOf('home') >= 0 || body.indexOf('Home') >= 0;
    var ready = !!sidebar && !loginRequired && (!!mainContent || hasHomeText);
    return JSON.stringify({ ready: ready, loginRequired: loginRequired, url: location.href, sidebar: !!sidebar, mainContent: !!mainContent, hasHomeText: hasHomeText, bodyStart: body.substring(0, 200) });
})()";

Console.WriteLine("Waiting for Wand SPA shell to load...");
string spaState = "";
int spaWaited = 0;
while (spaWaited < 30000)
{
    spaState = await EvalAsync(spaReadyJs);
    if (!string.IsNullOrEmpty(spaState))
    {
        using var state = JsonDocument.Parse(spaState);
        if (state.RootElement.GetProperty("ready").GetBoolean() ||
            state.RootElement.GetProperty("loginRequired").GetBoolean()) break;
    }
    await Task.Delay(1000);
    spaWaited += 1000;
}
Console.WriteLine($"SPA ready: {spaState}");

try
{
    using var sdoc = JsonDocument.Parse(spaState);
    if (sdoc.RootElement.TryGetProperty("loginRequired", out var lr) && lr.GetBoolean())
    {
        Console.WriteLine("ERROR: Wand is showing the login/welcome screen. A manual sign-in is required once. No data written.");
        return 6;
    }
    if (sdoc.RootElement.TryGetProperty("ready", out var rd) && rd.GetBoolean())
    {
        Console.WriteLine($"Wait OK: SPA sidebar (after {spaWaited / 1000}s)");
    }
    else
    {
        Console.WriteLine("ERROR: SPA did not become ready.");
        return 2;
    }
}
catch
{
    Console.WriteLine("ERROR: Failed to parse SPA state JSON.");
    return 2;
}

// ============ Step 3: Click Pulse nav ============
const string clickPulseNavJs = @"
(function(){
    var candidates = document.querySelectorAll('.app-sidebar__nav-entry, [class*=nav-entry], [class*=navEntry], [class*=sidebar] a, [class*=sidebar] div, a, button');
    for (var i = 0; i < candidates.length; i++) {
        var el = candidates[i];
        var t = (el.textContent || '').trim().toLowerCase();
        if (t === 'pulse' || (t.indexOf('pulse') >= 0 && t.length < 12)) {
            el.click();
            return 'clicked';
        }
    }
    return 'not-found';
})()";

Console.WriteLine("Navigating to Pulse page...");
string clickResult = await EvalAsync(clickPulseNavJs);
Console.WriteLine($"Click result: {clickResult}");
await Task.Delay(1500);

// ============ Step 4: Wait for Pulse list ============
const string pulseReadyJs = @"
(function(){
    var pulseContainer = document.querySelector('[class*=pulse-list], [class*=PulseList], [class*=pulseList], [class*=boost-list], [class*=BoostList]');
    var loading = document.querySelector('[class*=loading], [class*=spinner], [class*=skeleton]');
    var re = /[\d,]+\s*(点数|次助推|助推)/;
    var count = 0;
    if (pulseContainer) {
        var pointsNodes = pulseContainer.querySelectorAll('*');
        for (var i = 0; i < pointsNodes.length; i++) {
            if (pointsNodes[i].textContent && pointsNodes[i].textContent.match(re)) count++;
        }
    }
    if (count < 3) {
        var all = document.querySelectorAll('*');
        for (var i = 0; i < all.length; i++) {
            if (all[i].textContent && all[i].textContent.match(re)) count++;
        }
    }
    return JSON.stringify({ count: count, url: location.href, loading: !!loading });
})()";

Console.WriteLine("Waiting for Pulse list data...");
int pulseWaited = 0;
bool pulseReady = false;
string pulseJson = "";
while (pulseWaited < 90000)
{
    pulseJson = await EvalAsync(pulseReadyJs);
    if (!string.IsNullOrEmpty(pulseJson))
    {
        try
        {
            using var pdoc = JsonDocument.Parse(pulseJson);
            if (pdoc.RootElement.TryGetProperty("count", out var c) && c.GetInt32() >= 10)
            {
                pulseReady = true;
                break;
            }
        }
        catch { }
    }
    await Task.Delay(1500);
    pulseWaited += 1500;
}

if (!pulseReady)
{
    Console.WriteLine("ERROR: Pulse list did not render within 90s.");
    return 3;
}

Console.WriteLine($"Pulse data visible: {pulseJson}");
Console.WriteLine($"Wait OK: Pulse point badges (after {pulseWaited / 1000}s)");

// ============ Step 5: Scroll ============
const string scrollJs = @"
(function(){
    var el = document.scrollingElement || document.documentElement || document.body;
    el.scrollTop = el.scrollHeight;
    return 'scrolled-bottom:' + el.scrollTop;
})()";

string scrollResult = await EvalAsync(scrollJs);
Console.WriteLine($"Scroll: {scrollResult}");
await Task.Delay(1000);

// ============ Step 6: Extract (STRING 0 + platform) ============
// JS inside this verbatim string uses ONLY single quotes
// No double quotes appear in JS code below
const string extractScript = @"
(function(){
    var results = [];

    // Collect all game image alt texts
    var allImgs = document.querySelectorAll('img');
    var imgAltNames = [];
    for(var i = 0; i < allImgs.length; i++){
        var alt = allImgs[i].alt;
        if(alt && alt.trim().length > 2 && alt.trim().length < 100 && !alt.match(/^[\d\s]+$/)){
            imgAltNames.push(alt.trim());
        }
    }

    // Find main pulse container
    var pulseContainer = document.querySelector('[class*=pulse-list], [class*=PulseList], [class*=pulseList], [class*=boost-list], [class*=BoostList]');
    if(!pulseContainer) {
        var candidates = document.querySelectorAll('div');
        var best = null;
        var bestScore = 0;
        for(var c = 0; c < candidates.length; c++){
            var cand = candidates[c];
            var pointsCount = 0;
            var children = cand.querySelectorAll('*');
            for(var p = 0; p < children.length; p++){
                if(children[p].textContent && children[p].textContent.match(/[\d,]+\s*(点数|次助推|助推)/)) pointsCount++;
            }
            if(pointsCount > bestScore && pointsCount >= 3 && pointsCount <= 1000){
                bestScore = pointsCount;
                best = cand;
            }
        }
        pulseContainer = best;
    }

    if(!pulseContainer) {
        return JSON.stringify({method:'error', message:'no container found', imgs: imgAltNames});
    }

    // ====== Platform detection helpers ======
    function detectPlatformFromElement(el) {
        if (!el) return '';
        var cls = ((el.className || '') + ' ' + (el.getAttribute ? el.getAttribute('aria-label') : '')).toString().toLowerCase();
        var alt = ((el.alt || '') + '').toLowerCase();
        var cl = cls + ' ' + alt;
        if (cl.indexOf('xbox') >= 0) return 'Xbox';
        if (cl.indexOf('playstation') >= 0 || cl.indexOf('ps5') >= 0 || cl.indexOf('ps4') >= 0) return 'PlayStation';
        if (cl.indexOf('switch') >= 0) return 'Switch';
        if (cl.indexOf('epic') >= 0) return 'Epic';
        if (cl.indexOf('gog') >= 0) return 'GOG';
        if (cl.indexOf('steam') >= 0) return 'Steam';
        return '';
    }

    function detectPlatformText(child) {
        var textEls = child.querySelectorAll('span, small, strong, div, p, a');
        for (var n = 0; n < textEls.length; n++) {
            var t = (textEls[n].textContent || '').trim().toLowerCase();
            if (t === 'steam') return 'Steam';
            if (t === 'xbox') return 'Xbox';
            if (t === 'playstation' || t === 'ps5' || t === 'ps4') return 'PlayStation';
            if (t === 'switch' || t === 'nintendo switch') return 'Switch';
            if (t === 'epic' || t === 'epic games') return 'Epic';
            if (t === 'gog' || t === 'gog.com') return 'GOG';
        }
        var iconImgs = child.querySelectorAll('img');
        for (var ig = 0; ig < iconImgs.length; ig++) {
            var pf = detectPlatformFromElement(iconImgs[ig]);
            if (pf) return pf;
        }
        return '';
    }

    function detectPlatformCombined(text) {
        var raw = (text || '').toLowerCase();
        var platforms = [];
        if (raw.indexOf('steam') >= 0) platforms.push('Steam');
        if (raw.indexOf('xbox') >= 0) platforms.push('Xbox');
        if (raw.indexOf('playstation') >= 0 || raw.indexOf('ps5') >= 0 || raw.indexOf('ps4') >= 0) platforms.push('PlayStation');
        if (raw.indexOf('switch') >= 0 || raw.indexOf('nintendo') >= 0) platforms.push('Switch');
        if (raw.indexOf('epic') >= 0) platforms.push('Epic');
        if (raw.indexOf('gog') >= 0) platforms.push('GOG');
        return platforms.length ? platforms.join(' · ') : '';
    }

    function detectPlatform(child, childText) {
        var pf1 = detectPlatformText(child);
        if (pf1) return pf1;
        var allEls = child.querySelectorAll('*');
        for (var k = 0; k < allEls.length; k++) {
            var pf = detectPlatformFromElement(allEls[k]);
            if (pf) return pf;
        }
        return detectPlatformCombined(childText);
    }

    // ====== Strategy A: Direct children ======
    var children = pulseContainer.children;
    for(var i = 0; i < children.length; i++){
        var child = children[i];
        var childText = child.textContent.trim();
        if(!childText || childText.length > 800) continue;

        // Count leaf 次助推 elements in this child. If >1, this child wraps
        // multiple cards (e.g. the featured top-3 section) — skip it and let
        // Strategy B handle each card individually.
        var allDesc = child.querySelectorAll('*');
        var leafCount = 0;
        for(var d=0; d<allDesc.length; d++){
            var dt=(allDesc[d].textContent||'').trim();
            if(/(?:点数|次助推|助推)/.test(dt) && dt.length < 60) leafCount++;
        }
        if(leafCount > 3) continue;

        // Find the SHORTEST element containing 次助推 — the displayed number
        var pointsEls = child.querySelectorAll('*');
        var bestPointsText = '';
        for(var pe = 0; pe < pointsEls.length; pe++){
            var pt = (pointsEls[pe].textContent||'').trim();
            if(/(?:点数|次助推|助推)/.test(pt) && pt.length < 60 && (!bestPointsText || pt.length < bestPointsText.length)){
                bestPointsText = pt;
            }
        }
        if(!bestPointsText) bestPointsText = childText;
        var pointsMatch = bestPointsText.match(/([\d,]+)\s*(?:点数|次助推|助推)/);
        if(!pointsMatch) continue;

        var rankMatch = childText.match(/^\s*(\d+)\s*[.、]?/m);
        var rank = rankMatch ? rankMatch[1] : '';
        var points = pointsMatch[1].replace(/,/g, '');

        var platform = detectPlatform(child, childText);

        var name = '';
        // 1. img alt
        var childImgs = child.querySelectorAll('img');
        for(var ig = 0; ig < childImgs.length; ig++){
            var alt = childImgs[ig].alt;
            if(alt && alt.trim().length > 2 && !alt.match(/^[\d\s,.]+$/)){
                name = alt.trim();
                break;
            }
        }
        // 2. known selectors
        if(!name){
            var nameEl = child.querySelector('[class*=game-name], [class*=gamename], [class*=game_title], [class*=GameName], h1, h2, h3, h4');
            if(nameEl && nameEl.textContent){
                name = nameEl.textContent.trim();
            }
        }
        // 3. score text
        if(!name){
            var textEls = child.querySelectorAll('span, p, div, [class*=name], [class*=title], [class*=label]');
            var bestName = '';
            var bestScore = 0;
            for(var te = 0; te < textEls.length; te++){
                var tn = textEls[te].textContent.trim();
                if(tn.length < 3 || tn.length > 120) continue;
                if(tn.match(/^[\d\s,.:：·•\-—点数投票]+$/)) continue;
                if(tn.match(/点数|投票|double_arrow|local_fire_department/)) continue;
                var score = tn.length * 3 + (tn.match(/[a-zA-Z]/g) ? tn.match(/[a-zA-Z]/g).length * 5 : 0);
                if(score > bestScore){
                    bestScore = score;
                    bestName = tn;
                }
            }
            name = bestName;
        }
        // 4. match img alt names
        if(!name || name.match(/^[\d\s,.点数投票]+$/)){
            for(var ia = 0; ia < imgAltNames.length; ia++){
                var guess = imgAltNames[ia];
                if(guess && guess.length >= 3 && !guess.match(/^[\d\s]+$/)){
                    var alreadyUsed = results.some(function(r){ return r.name === guess; });
                    if(!alreadyUsed){ name = guess; break; }
                }
            }
        }
        // 5. text extraction
        if(!name || name.match(/^[\d\s,.点数投票]+$/)){
            name = childText
                .replace(/^\s*\d+\s*[.、]?/m, '')
                .replace(/[\d,]+\s*(?:点数|次助推|助推)/g, '')
                .replace(/投票/g, '')
                .replace(/double_arrow/g, '')
                .replace(/local_fire_department/g, '')
                .replace(/Steam\s*[·•]\s*/g, '')
                .replace(/Xbox\s*[·•]\s*/g, '')
                .replace(/PlayStation\s*[·•]\s*/g, '')
                .replace(/Switch\s*[·•]\s*/g, '')
                .replace(/Epic\s*[·•]\s*/g, '')
                .replace(/GOG\s*[·•]\s*/g, '')
                .replace(/\s+/g, ' ')
                .trim();
            name = name.split(/\n/)[0].trim();
        }
        // Final name clean: strip any leftover points / UI text
        name = name.replace(/[\d,]+\s*(?:点数|次助推|助推)/g, '').replace(/投票/g, '').replace(/\s+/g, ' ').trim();
        if(!name || name.length < 2) continue;
        if(name.match(/^[\d\s,.\-:：·•点数投票double_arrow]+$/)) continue;
        if(name.length > 100) name = name.substring(0, 100);
        // Deduplicate: skip if same game name already captured (higher rank wins)
        if(results.some(function(r){ return r.name.toLowerCase() === name.toLowerCase(); })) continue;

        results.push({ rank: rank, name: name.trim(), points: points, platform: platform });
    }

    // ====== Strategy B: leaf-based (handles featured cards) ======
    if(results.length < 25) {
        function elDepth(el){ var d=0; while(el && el.parentElement){ d++; el=el.parentElement; } return d; }
        // Find all leaf elements containing 次助推 (short text = the displayed badge)
        var allEls = pulseContainer.querySelectorAll('*');
        var leaves = [];
        for(var i = 0; i < allEls.length; i++){
            var lt = (allEls[i].textContent||'').trim();
            if(/(?:点数|次助推|助推)/.test(lt) && lt.length >= 8 && lt.length < 60){
                leaves.push({el: allEls[i], depth: elDepth(allEls[i])});
            }
        }
        // Deepest first — the displayed badge is more specific (deeper) than
        // any aggregated total that may exist in a parent element.
        leaves.sort(function(a,b){ return b.depth - a.depth; });
        for(var li = 0; li < leaves.length; li++){
            var leaf = leaves[li].el;
            var leafText = leaf.textContent.trim();
            var pm = leafText.match(/([\d,]+)\s*(?:点数|次助推|助推)/);
            if(!pm) continue;
            var points = pm[1].replace(/,/g, '');
            // Dedup by points already captured
            // Equal point totals do not identify a game.
            // Walk up from leaf, trying each ancestor to find the game name.
            // No text length limit — some cards have long names; we just need
            // the first ancestor where a clean name can be extracted.
            var cardEl = leaf, name = '';
            for(var up = 0; up < 15 && cardEl; up++){
                var ct = (cardEl.textContent||'').trim();
                if(ct.length > 0){
                    var cImgs = cardEl.querySelectorAll('img');
                    for(var ak=0; ak<cImgs.length; ak++){
                        if(cImgs[ak].alt && cImgs[ak].alt.trim().length > 3 && cImgs[ak].alt.trim().length < 120
                           && !cImgs[ak].alt.match(/^[\d\s,.]+$/)){ name = cImgs[ak].alt.trim(); break; }
                    }
                    if(!name){
                        var cleaned = ct.replace(/[\d,]+\s*(?:点数|次助推|助推)/g,'').replace(/^\s*\d{1,2}\s*/,'')
                                        .replace(/投票/g,'').replace(/\b(?:Steam|Game Pass|Xbox|PlayStation|PS[45]|Switch|Epic|GOG)\s*[·•]\s*/g,'')
                                        .replace(/\s+/g,' ').trim().split('  ')[0].split(' · ')[0].trim();
                        if(cleaned.length >= 2 && cleaned.length < 120 && cleaned.match(/[a-zA-Z\u4e00-\u9fff]/)
                           && !cleaned.match(/^[\d\s,.·•]+$/)){ name = cleaned; }
                    }
                    if(name) break;
                }
                cardEl = cardEl.parentElement;
            }
            var cardText = (cardEl.textContent||'').trim();
            // Strip platform suffixes and UI text from name
            name = name.replace(/[\d,]+\s*(?:点数|次助推|助推)/g,'').replace(/投票/g,'')
                       .replace(/\b(Steam|Game Pass|Xbox|PlayStation|PS[45]|Switch|Epic|GOG)\s*[·•]?\s*$/i, '')
                       .replace(/\s+/g,' ').trim();
            if(!name || name.length < 2 || name.match(/^[\d\s,.]+$/)) continue;
            // Dedup by name — keep first (deepest = displayed number, not total)
            if(results.some(function(r){ return r.name.toLowerCase() === name.toLowerCase(); })) continue;
            var platformB = detectPlatform(cardEl || leaf, cardText || leafText);
            results.push({ rank: '', name: name.substring(0,100).trim(), points: points, platform: platformB });
        }
    }

    var byName = {};
    for(var i=0;i<results.length;i++){
        var r=results[i], key=r.name.toLowerCase().trim();
        if(!byName[key]) byName[key]=r;
    }
    var finalResults=Object.keys(byName).map(function(k){return byName[k];});
    finalResults.sort(function(a,b){ return parseInt(b.points) - parseInt(a.points); });
    for(var i = 0; i < finalResults.length; i++){
        if(!finalResults[i].rank || parseInt(finalResults[i].rank) !== (i+1)){
            finalResults[i].rank = String(i+1);
        }
    }
    for(var i = 0; i < finalResults.length; i++){ delete finalResults[i]._score; }

    return JSON.stringify({method:'pulse-cards', count: finalResults.length, data: finalResults.slice(0, 30)});
})()";

Console.WriteLine("Extracting Pulse data...");
string extractResult = "";
for (int attempt = 1; attempt <= 3; attempt++)
{
    Console.WriteLine($"Extract attempt {attempt}/3...");
    extractResult = await EvalAsync(extractScript);
    if (!string.IsNullOrEmpty(extractResult))
    {
        try
        {
            using var edoc = JsonDocument.Parse(extractResult);
            if (edoc.RootElement.TryGetProperty("count", out var cnt) && cnt.GetInt32() >= 5)
            {
                Console.WriteLine($"Extract result count: {cnt.GetInt32()}");
                break;
            }
        }
        catch { }
    }
    await Task.Delay(1000);
}

if (string.IsNullOrEmpty(extractResult))
{
    Console.WriteLine("ERROR: Failed to extract data after 3 attempts. No data written.");
    return 4;
}

Console.WriteLine($"=== Pulse Data ===\n{extractResult}");

// ============ Step 7: Save CSV (5 columns) ============
using var resultDoc = JsonDocument.Parse(extractResult);
if (!resultDoc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
{
    Console.WriteLine("ERROR: No data array in extraction result.");
    return 4;
}

var games = new List<(int Rank, string Name, long Points, string Platform)>();
foreach (var item in dataArr.EnumerateArray())
{
    if (!int.TryParse(item.GetProperty("rank").GetString(), out var rank)) continue;
    var name = item.GetProperty("name").GetString() ?? "";
    if (!long.TryParse(item.GetProperty("points").GetString(), out var points)) continue;
    string platform = "";
    if (item.TryGetProperty("platform", out var plat)) platform = plat.GetString() ?? "";
    games.Add((rank, name, points, platform));
}

var minGames = int.TryParse(Environment.GetEnvironmentVariable("WAND_MIN_GAMES"), out var configuredMin) ? configuredMin : 20;
if (games.Count < minGames || games.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != games.Count ||
    games.Any(g => g.Points < 0 || g.Name.Contains("邀请好友") || g.Name.Contains("获得奖励")))
{
    Console.WriteLine("ERROR: No valid game rows extracted.");
    return 5;
}

string CsvEscape(string s)
{
    if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    return s;
}

var csvHeader = "序号,游戏名称,点数,获取时间,平台";
var fetchTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

// Append this snapshot to the CSV (production behavior).
// The monitor groups rows by 获取时间 and compares days, so history must accumulate.
var csvDir = Path.GetDirectoryName(csvPath);
if (!string.IsNullOrEmpty(csvDir)) Directory.CreateDirectory(csvDir);

var needHeader = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
// UTF-8 without BOM: when appending, StreamWriter never inserts a BOM mid-file.
using (var sw = new StreamWriter(csvPath, append: true, new UTF8Encoding(false)))
{
    if (needHeader) sw.WriteLine(csvHeader);
    foreach (var g in games)
    {
        sw.WriteLine($"{g.Rank},{CsvEscape(g.Name)},{g.Points},{fetchTime},{CsvEscape(g.Platform)}");
    }
}
Console.WriteLine($"Appended {games.Count} games (snapshot {fetchTime}) to {csvPath}");

return 0;
