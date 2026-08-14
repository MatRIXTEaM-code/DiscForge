// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DiscForge.Cli;

/// <summary>
/// A modern, cross-platform browser UI over the DiscForge engine — the polished interface the
/// frozen legacy burners never grew. It runs a tiny local HTTP server (loopback only) that serves
/// a single self-contained page and executes `dforge` commands on demand by invoking THIS binary
/// as a subprocess, so every one of the 286 CLI verbs is reachable from the browser with real
/// output. No external framework, no telemetry, no network exposure beyond 127.0.0.1.
/// </summary>
internal static class UiServer
{
    public static int Run(int port, bool openBrowser)
    {
        string prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Could not start the UI server on {prefix}: {ex.Message}");
            Console.Error.WriteLine("Try a different port with --port, or check nothing else is using it.");
            return 1;
        }

        Console.WriteLine($"DiscForge UI running at {prefix}");
        Console.WriteLine("Press Ctrl+C to stop.");
        if (openBrowser) TryOpenBrowser(prefix);

        using var _ = listener;
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (InvalidOperationException) { break; }
            try { Handle(ctx); }
            catch (Exception ex) { TryWrite(ctx, 500, "text/plain", Encoding.UTF8.GetBytes(ex.Message)); }
        }
        return 0;
    }

    private static void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path == "/" && ctx.Request.HttpMethod == "GET")
        {
            TryWrite(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(IndexHtml));
            return;
        }
        if (path == "/api/run" && ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            string body = reader.ReadToEnd();
            string commandLine = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                commandLine = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            }
            catch (JsonException) { }

            var result = RunCommand(commandLine);
            var json = JsonSerializer.SerializeToUtf8Bytes(result);
            TryWrite(ctx, 200, "application/json", json);
            return;
        }
        TryWrite(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
    }

    private sealed record RunResult(int exitCode, string stdout, string stderr);

    private static RunResult RunCommand(string commandLine)
    {
        var args = SplitArgs(commandLine);
        if (args.Count == 0) return new RunResult(1, "", "Empty command.");

        // Re-invoke THIS binary as a subprocess so the command runs with a clean Console and no
        // shared static state. Works whether launched as `dforge` (native) or `dotnet dforge.dll`.
        string self = Assembly.GetEntryAssembly()?.Location ?? "";
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (self.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(self);
        }
        else
        {
            psi.FileName = self.Length > 0 ? self : (Environment.ProcessPath ?? "dforge");
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            string outp = proc.StandardOutput.ReadToEnd();
            string errp = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);
            return new RunResult(proc.HasExited ? proc.ExitCode : -1, outp, errp);
        }
        catch (Exception ex)
        {
            return new RunResult(-1, "", "Failed to run command: " + ex.Message);
        }
    }

    /// <summary>Split a command line into args, honouring "double" and 'single' quotes.</summary>
    internal static List<string> SplitArgs(string line)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        bool inTok = false;
        foreach (char ch in line)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
            }
            else if (ch is '"' or '\'') { quote = ch; inTok = true; }
            else if (char.IsWhiteSpace(ch))
            {
                if (inTok) { args.Add(cur.ToString()); cur.Clear(); inTok = false; }
            }
            else { cur.Append(ch); inTok = true; }
        }
        if (inTok) args.Add(cur.ToString());
        return args;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { /* headless / no browser — the URL is printed above */ }
    }

    private static void TryWrite(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
        }
        catch { /* client went away */ }
    }

    private const string IndexHtml = """
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DiscForge</title>
<style>
:root{--bg:#0f1216;--panel:#171c22;--line:#252c34;--text:#e6eaef;--muted:#8b97a5;--accent:#4c9ffe;--ok:#2ecc71;--bad:#e74c3c}
*{box-sizing:border-box}body{margin:0;font:14px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--text)}
header{padding:14px 20px;border-bottom:1px solid var(--line);display:flex;align-items:center;gap:12px}
header h1{font-size:16px;margin:0;font-weight:650;letter-spacing:.2px}
header .tag{color:var(--muted);font-size:12px}
main{display:grid;grid-template-columns:240px 1fr;gap:0;height:calc(100vh - 51px)}
aside{border-right:1px solid var(--line);padding:14px;overflow:auto}
aside h2{font-size:11px;text-transform:uppercase;letter-spacing:.8px;color:var(--muted);margin:16px 0 6px}
button.act{display:block;width:100%;text-align:left;background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:8px;padding:8px 10px;margin:4px 0;cursor:pointer;font-size:13px}
button.act:hover{border-color:var(--accent)}
section{display:flex;flex-direction:column;min-width:0}
.bar{display:flex;gap:8px;padding:14px;border-bottom:1px solid var(--line)}
.bar input{flex:1;background:var(--panel);border:1px solid var(--line);border-radius:8px;color:var(--text);padding:10px 12px;font:13px ui-monospace,Menlo,Consolas,monospace}
.bar button{background:var(--accent);color:#06121f;border:0;border-radius:8px;padding:0 18px;font-weight:650;cursor:pointer}
.out{flex:1;overflow:auto;padding:14px 16px;margin:0;white-space:pre-wrap;font:12.5px/1.55 ui-monospace,Menlo,Consolas,monospace;color:#d8dee6}
.pill{font-size:11px;padding:2px 8px;border-radius:999px;border:1px solid var(--line)}
.pill.ok{color:var(--ok);border-color:#1f5c3a}.pill.bad{color:var(--bad);border-color:#5c2a24}
.hint{color:var(--muted);padding:6px 16px;font-size:12px}
</style></head><body>
<header><h1>DiscForge</h1><span class="tag">local UI · 127.0.0.1</span><span id="status"></span></header>
<main>
 <aside>
  <h2>Discover</h2>
  <button class="act" data-cmd="drives">List drives</button>
  <button class="act" data-tpl="identify {file}">Identify an image…</button>
  <button class="act" data-tpl="inspect-raw {file} --deep">Inspect raw image…</button>
  <h2>Plan</h2>
  <button class="act" data-tpl="disc-span {folder} --media bd25">Span a folder → BD-25…</button>
  <button class="act" data-tpl="disc-span {folder} --media dvd9 --keep-groups">Span → DVD-9 (keep folders)…</button>
  <button class="act" data-tpl="capacity-check {sectors} bd25">Capacity check…</button>
  <h2>Source</h2>
  <button class="act" data-tpl="source-stage {manifest} {stagingdir}">Stage from manifest…</button>
  <h2>Help</h2>
  <button class="act" data-cmd="help">All commands</button>
 </aside>
 <section>
  <div class="bar">
   <input id="cmd" placeholder="type a dforge command, e.g.  disc-span /path/to/folder --media bd25" autofocus>
   <button id="run">Run</button>
  </div>
  <div class="hint">Runs against this machine's DiscForge binary. Fill in {placeholders} before running.</div>
  <pre class="out" id="out">Ready. Pick an action on the left, or type a command above.</pre>
 </section>
</main>
<script>
const $=s=>document.querySelector(s), out=$('#out'), cmd=$('#cmd'), status=$('#status');
function setStatus(code){status.innerHTML = code===0?'<span class="pill ok">exit 0</span>':'<span class="pill bad">exit '+code+'</span>';}
async function run(line){
 if(!line.trim())return;
 out.textContent='Running: '+line+'\n\n'; status.innerHTML='';
 try{
  const r=await fetch('/api/run',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:line})});
  const j=await r.json();
  out.textContent=(j.stdout||'')+(j.stderr?('\n'+j.stderr):'');
  if(!out.textContent.trim())out.textContent='(no output)';
  setStatus(j.exitCode);
 }catch(e){out.textContent='UI error: '+e;}
}
$('#run').onclick=()=>run(cmd.value);
cmd.addEventListener('keydown',e=>{if(e.key==='Enter')run(cmd.value);});
document.querySelectorAll('button.act').forEach(b=>b.onclick=()=>{
 if(b.dataset.cmd){cmd.value=b.dataset.cmd;run(b.dataset.cmd);}
 else{cmd.value=b.dataset.tpl;cmd.focus();
  const i=cmd.value.indexOf('{');if(i>=0)cmd.setSelectionRange(i,cmd.value.indexOf('}',i)+1);}
});
</script></body></html>
""";
}
