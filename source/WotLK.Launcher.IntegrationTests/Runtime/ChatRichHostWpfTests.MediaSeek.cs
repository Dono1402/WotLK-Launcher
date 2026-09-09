using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ChatRichHostWpfTests
{
    // Optional extended QA with generated, decoded-and-verified silent fixtures.
    // The ordinary host suite remains self-contained without FFmpeg installed.
    private static async Task ValidateLocalMediaSeekingAsync(ChatViewV2 view, Window window, CoreWebView2 core, string directory)
    {
        string? fixtures = Environment.GetEnvironmentVariable("ATLAS_CHAT_SEEK_FIXTURES");
        if (string.IsNullOrWhiteSpace(fixtures)) return;
        string? baselinePath = Environment.GetEnvironmentVariable("ATLAS_CHAT_SEEK_BASELINE");
        string? baseline = string.IsNullOrWhiteSpace(baselinePath) ? null : await File.ReadAllTextAsync(baselinePath);
        Func<string, string?, CancellationToken, Task<ChatMediaStream?>>? originalResolver = view.MediaResolver;
        ChatAttachmentFileSource files = new(Path.Combine(directory, "seek-staged"), new Uri("https://fixture.invalid/"));
        MethodInfo createLocalMedia = typeof(LauncherChatWorkspace).GetMethod("CreateLocalMedia", BindingFlags.Static | BindingFlags.NonPublic)!;
        List<object> samples = [], requests = [];
        await core.ExecuteScriptAsync("window.atlasOriginalMediaApi=AtlasChatMedia");
        try
        {
            foreach (string version in baseline is null ? new[] { "after" } : new[] { "before", "after" })
            {
                if (version == "before") await core.ExecuteScriptAsync(baseline!);
                else await core.ExecuteScriptAsync("window.AtlasChatMedia=window.atlasOriginalMediaApi");
                foreach ((string name, string kind) in new[] { ("seek-audio-70s.mp3", "audio"), ("seek-video-audio-70s.mp4", "video") })
                {
                    string resource = "attachments/native-seek-" + version + "-" + kind;
                    ChatSelectedFile staged = await files.InspectAsync(42, Path.GetFullPath(Path.Combine(fixtures, name)), CancellationToken.None);
                    ChatLocalUpload upload = new() { LocalId = "native-seek", ThreadId = "17", SourcePath = staged.SourcePath,
                        FileName = staged.FileName, ContentType = staged.ContentType, Size = staged.Size, LastWriteAt = staged.LastWriteAt, Sha256 = staged.Sha256 };
                    view.MediaResolver = async (key, range, token) =>
                    {
                        if (key != resource) return originalResolver is null ? null : await originalResolver(key, range, token);
                        Stopwatch elapsed = Stopwatch.StartNew();
                        Stream stream = await files.OpenPreviewAsync(upload, token);
                        ChatMediaStream result = (ChatMediaStream)createLocalMedia.Invoke(null, new object?[] { stream, upload, range, token })!;
                        requests.Add(new { version, name, range, headersMs = elapsed.Elapsed.TotalMilliseconds });
                        return result;
                    };
                    await core.ExecuteScriptAsync($$$"""
                        (() => {
                          const player=document.createElement('{{{kind}}}'); player.preload='metadata'; player.muted=false;
                          player.src='https://atlas-chat-media.invalid/{{{resource}}}';
                          const shell=AtlasChatMedia.create(player,{mode:'draft'});
                          shell.style.cssText='position:fixed;left:20px;top:20px;width:600px;height:240px;z-index:99';
                          document.body.append(shell);window.atlasSeek={player,shell};window.atlasSeekTrace=[];
                          const nativeTime=Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype,'currentTime');
                          Object.defineProperty(player,'currentTime',{configurable:true,get(){return nativeTime.get.call(this)},set(value){atlasSeekTrace.push({type:'set-time',value,at:performance.now()});nativeTime.set.call(this,value)}});
                          for(const type of ['seeking','seeked','waiting','playing','pause'])player.addEventListener(type,()=>atlasSeekTrace.push({type,time:player.currentTime,at:performance.now()}));
                        })()
                        """);
                    await UntilScript(core, "atlasSeek.player.readyState>=3&&atlasSeek.player.duration>69&&!atlasSeek.player.error", $"The local staged {name} decodes before playback.");
                    if (kind == "video") await UntilScript(core, "atlasSeek.player.videoWidth===320&&atlasSeek.player.videoHeight===180", "The native MP4 fixture has decoded H264 video frames.");
                    await core.ExecuteScriptAsync("atlasSeek.shell.querySelector('.media-play').click()");
                    await UntilScript(core, "!atlasSeek.player.paused&&atlasSeek.player.currentTime>.03", $"The silent unmuted {name} plays through the native draft stream.");
                    foreach (int seconds in new[] { 28, 52, 35 })
                    {
                        using JsonDocument point = JsonDocument.Parse(await core.ExecuteScriptAsync($$"""
                            (()=>{const p=atlasSeek.player,r=atlasSeek.shell.querySelector('.media-seek').getBoundingClientRect();
                              window.atlasSeekBefore={buffered:Array.from({length:p.buffered.length},(_,i)=>[p.buffered.start(i),p.buffered.end(i)]),readyState:p.readyState};
                              window.atlasSeekTrace=[];return{x:r.x+4.5+(r.width-9)*{{seconds}}/p.duration,y:r.y+r.height/2};})()
                            """));
                        double x = point.RootElement.GetProperty("x").GetDouble(), y = point.RootElement.GetProperty("y").GetDouble();
                        int requestsBefore = requests.Count;
                        foreach (string type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", clickCount = type == "mouseMoved" ? 0 : 1 }));
                        await UntilScript(core, $"!atlasSeek.player.seeking&&!atlasSeek.player.paused&&Math.abs(atlasSeek.player.currentTime-{seconds})<.4&&atlasSeekTrace.some(e=>e.type==='playing')", $"Native {name} resumes after a click near {seconds} seconds.");
                        using JsonDocument result = JsonDocument.Parse(await core.ExecuteScriptAsync("({before:atlasSeekBefore,currentTime:atlasSeek.player.currentTime,paused:atlasSeek.player.paused,muted:atlasSeek.player.muted,videoWidth:atlasSeek.player.videoWidth,src:atlasSeek.player.currentSrc,trace:atlasSeekTrace})"));
                        if (version == "after") True(result.RootElement.GetProperty("trace").EnumerateArray().Count(entry => entry.GetProperty("type").GetString() == "set-time") == 1,
                            $"One native position change for the {name} click at {seconds} seconds.");
                        samples.Add(new { version, name, seconds, newRequests = requests.Count - requestsBefore, state = result.RootElement.Clone() });
                    }
                    await core.ExecuteScriptAsync("AtlasChatMedia.dispose(atlasSeek.shell);atlasSeek.shell.remove();delete window.atlasSeek;delete window.atlasSeekTrace;delete window.atlasSeekBefore");
                    await files.DeleteStagedAsync(42, staged.SourcePath, CancellationToken.None);
                }
            }
            True(!window.IsActive && window.Left < -10000 && !window.ShowInTaskbar, "Native seek gestures remain inside the inactive offscreen fixture.");
            await File.WriteAllTextAsync(Path.Combine(directory, "native-media-seek.json"), JsonSerializer.Serialize(new { samples, requests,
                externalNetwork = false, userSession = false, method = "Physical staged silent files, OpenPreviewAsync + CreateLocalMedia + WebView2 response stream, unmuted MP3 and H264/AAC, CDP input inside offscreen fixture." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            view.MediaResolver = originalResolver;
            await core.ExecuteScriptAsync("if(window.atlasSeek){AtlasChatMedia.dispose(atlasSeek.shell);atlasSeek.shell.remove();delete window.atlasSeek;}window.AtlasChatMedia=window.atlasOriginalMediaApi;delete window.atlasOriginalMediaApi");
        }
    }
}
