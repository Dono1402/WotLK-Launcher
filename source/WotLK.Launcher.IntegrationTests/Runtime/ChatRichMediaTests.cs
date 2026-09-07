using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Server;

internal static class ChatRichMediaTests
{
    private static readonly CancellationToken None=CancellationToken.None;
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        _checks=0;string root=Path.Combine(Path.GetTempPath(),"atlas-chat-media-fixture-"+Guid.NewGuid().ToString("N"));
        try{PlainText();await Attachments(new ChatAttachmentStorage(root));await Links();_checks+=await ChatAttachmentFormatTests.RunAsync(root);Console.WriteLine($"Chat rich media PASS: {_checks} assertions. Markdown AST to game text, private owned/resumable storage, format signatures, full UTF8 validation, office ZIP markers, byte limits and rollback; public URL/IP/redirect filtering, provider allowlist, inert metadata and bounded media. Mock HTTP only, no external network.");return 0;}
        finally{string full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("atlas-chat-media-fixture-",StringComparison.Ordinal))throw new InvalidOperationException("Unsafe media fixture cleanup.");if(Directory.Exists(full))Directory.Delete(full,true);}
    }

    private static void PlainText()
    {
        foreach(var(markdown,expected)in new(string,string)[]
        {
            ("**gras** et *italique* et ~~barré~~","gras et italique et barré"),
            ("[site Atlas](https://example.com/path)","site Atlas (https://example.com/path)"),
            ("[https://example.com](https://example.com)","https://example.com"),
            ("`**literal** ||pipe||`","**literal** ||pipe||"),
            ("```text\n**code**\n```","**code**"),
            ("- premier\n- second","- premier\n- second"),
            ("||secret **important**||","secret important"),
            ("<b>texte utilisateur</b>","<b>texte utilisateur</b>"),
            ("[référence][r]\n\n[r]: https://example.com/retained","référence (https://example.com/retained)"),
            ("\\*literal\\*","*literal*")
        })Check(ChatPlainTextProjection.ForLegacy(markdown)==expected,"Structured Markdown projection: "+markdown);
        string repeated=string.Join(' ',Enumerable.Repeat("[x][r]",100))+"\n\n[r]: https://example.com/"+new string('a',300);
        string bounded=ChatPlainTextProjection.ForLegacy(repeated);Check(bounded.Length<=1000&&bounded.EndsWith('…'),"Expanded reference URLs remain inside legacy limit.");
    }

    private static async Task Attachments(ChatAttachmentStorage storage)
    {
        foreach(string name in new[]{"archive.zip","archive.7z","archive.rar","app.exe","vector.svg","script.html","../file.txt","C:\\secret.txt","a\u202e.txt"})await Error(()=>storage.BeginAsync(1,new(name,"application/octet-stream",1),None));
        await Error(()=>storage.BeginAsync(1,new("big.txt","text/plain",ChatLimits.MaximumAttachmentBytes+1),None),"chat-request-too-large");
        ChatUploadDto maximum=await storage.BeginAsync(1,new("max.txt","text/plain",ChatLimits.MaximumAttachmentBytes),None);Check(maximum.Size==500_000_000&&maximum.Offset==0,"Exactly500Mo accepted without allocating its payload.");await storage.CancelAsync(1,maximum.Id,None);
        ChatUploadDto text=await storage.BeginAsync(1,new("日本語.txt","text/plain",Encoding.UTF8.GetByteCount("Bonjour 世界")),None);
        await Error(()=>storage.GetAsync(2,text.Id,None),"chat-upload-not-found");
        await Error(()=>storage.RequireCompleteAsync(1,text.Id,None),"chat-upload-incomplete");
        await storage.AppendAsync(1,text.Id,0,new MemoryStream(Encoding.UTF8.GetBytes("Bonjour 世界")),text.Size,None);
        ChatUploadDto complete=await storage.CompleteAsync(1,text.Id,None);Check(complete.IsComplete&&complete.Attachment?.ContentType.StartsWith("text/plain",StringComparison.Ordinal)==true,"UTF8 text accepted with server-derived MIME.");
        Check((await storage.CompleteAsync(1,text.Id,None)).Attachment?.Sha256==complete.Attachment?.Sha256,"Complete idempotent.");
        await storage.CancelAsync(1,text.Id,None);Check((await storage.RequireCompleteAsync(1,text.Id,None)).Id==text.Id,"Draft cancellation cannot destroy complete media.");
        ChatAttachmentRead opened=await storage.OpenReadAsync(text.Id,None);await using(opened.Stream){using StreamReader reader=new(opened.Stream);Check(await reader.ReadToEndAsync()=="Bonjour 世界","Stored bytes round trip.");}

        ChatUploadDto chunk=await storage.BeginAsync(1,new("chunks.txt","text/plain",ChatLimits.UploadChunkBytes+2),None);
        await storage.AppendAsync(1,chunk.Id,0,new MemoryStream("A"u8.ToArray()),1,None);
        await Error(()=>storage.AppendAsync(1,chunk.Id,0,new MemoryStream("B"u8.ToArray()),1,None),"chat-upload-offset-conflict");
        await Error(()=>storage.AppendAsync(1,chunk.Id,1,new PatternStream(ChatLimits.UploadChunkBytes+1),null,None),"chat-request-too-large");
        Check((await storage.GetAsync(1,chunk.Id,None)).Offset==1,"Oversized streaming chunk rolls offset back.");
        bool interrupted=false;try{await storage.AppendAsync(1,chunk.Id,1,new ThrowingStream(),null,None);}catch(IOException){interrupted=true;}Check(interrupted&&(await storage.GetAsync(1,chunk.Id,None)).Offset==1,"Interrupted chunk rolls back persisted progress.");
        await storage.AppendAsync(1,chunk.Id,1,new PatternStream(ChatLimits.UploadChunkBytes),ChatLimits.UploadChunkBytes,None);
        await storage.AppendAsync(1,chunk.Id,ChatLimits.UploadChunkBytes+1,new MemoryStream("Z"u8.ToArray()),1,None);
        Check((await storage.CompleteAsync(1,chunk.Id,None)).IsComplete,"Upload resumes exactly after rollback.");

        foreach(var fixture in new (string Name,byte[] Bytes)[]{("bad.png","MZ bad content"u8.ToArray()),("bad.pdf","not a PDF"u8.ToArray()),("bad.mp4","not video"u8.ToArray()),("bad.webm","fake EBML"u8.ToArray()),("bad.txt",[0xc3,0x28]),("truncated.txt",[0xc3]),("nul.txt",[65,0,66])})
            await RejectContent(storage,fixture.Name,fixture.Bytes);
        byte[] lateInvalid=Enumerable.Repeat((byte)'A',65537).ToArray();lateInvalid[^1]=0xff;await RejectContent(storage,"late-invalid.txt",lateInvalid);
        byte[] boundary=Encoding.UTF8.GetBytes(new string('a',65535)+"世界");Check((await Upload(storage,"boundary.txt",boundary)).IsComplete,"Multibyte UTF8 crossing read boundary remains valid.");
        foreach(var fixture in new (string Name,byte[] Bytes)[]{("sample.png",[137,80,78,71,13,10,26,10,0]),("sample.jpg",[255,216,255,0]),("personal.gif","GIF89a0000"u8.ToArray()),("sample.pdf","%PDF-1.7\n%%EOF"u8.ToArray())})
            Check((await Upload(storage,fixture.Name,fixture.Bytes)).IsComplete,"Recognized signature: "+fixture.Name);
        byte[] office=Zip(new(){["[Content_Types].xml"]="<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>",["_rels/.rels"]="<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'/>",["word/document.xml"]="<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'/>"});
        Check((await Upload(storage,"sample.docx",office)).IsComplete,"Office package recognized by required parts.");
        await RejectContent(storage,"disguised.docx",Zip(new(){["data.txt"]="ordinary archive"}));
        await RejectContent(storage,"macro.docx",Zip(new(){["[Content_Types].xml"]="<Types/>",["_rels/.rels"]="<Relationships/>",["word/document.xml"]="<document/>",["word/vbaProject.bin"]="macro"}));
        Check((await Upload(storage,"sample.odt",Zip(new(){["mimetype"]="application/vnd.oasis.opendocument.text",["content.xml"]="<office:document xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'/>"}))).IsComplete,"ODF MIME marker validated.");
        await RejectContent(storage,"wrong.odt",Zip(new(){["mimetype"]="application/zip",["content.xml"]="<content/>"}));
    }

    private static async Task Links()
    {
        foreach(string url in new[]{"file:///C:/secret","javascript:alert(1)","https://user:pass@example.com/a","https://localhost/a","http://127.0.0.1/a","http://127.1/a","http://2130706433/a","http://10.0.0.1/a","http://172.16.1.1/a","http://192.168.1.1/a","http://169.254.169.254/a","http://100.64.0.1/a","http://0.0.0.0/a","http://[::1]/a","http://[::ffff:127.0.0.1]/a","https://service.local/a","https://example.com:8443/a"})Check(!ChatPublicUrlPolicy.TryNormalize(url,out _),"Unsafe URL syntax rejected: "+url);
        Check(ChatPublicUrlPolicy.TryNormalize("https://example.com/path",out _),"Public HTTPS syntax accepted.");
        Check(ChatPublicUrlPolicy.IsPublicAddress(IPAddress.Parse("8.8.8.8"))&&ChatPublicUrlPolicy.IsPublicAddress(IPAddress.Parse("2001:4860:4860::8888")),"Public IPv4/IPv6 accepted.");
        foreach(string ip in new[]{"224.0.0.1","198.18.0.1","192.0.2.1","203.0.113.1","fc00::1","fe80::1","2001:db8::1","2002:0808:0808::1"})Check(!ChatPublicUrlPolicy.IsPublicAddress(IPAddress.Parse(ip)),"Nonpublic or transition address excluded.");
        IReadOnlyList<Uri> urls=ChatLinkPreviewService.ExtractUrls("https://example.com/a, https://example.com/a https://example.org/b). http://127.0.0.1/");Check(urls.Count==2&&urls[0].AbsolutePath=="/a"&&urls[1].AbsolutePath=="/b","Ordered URL extraction deduplicates and strips punctuation.");
        Check(ChatLinkPreviewService.ExtractUrls(string.Join(' ',Enumerable.Range(0,20).Select(i=>"https://example.com/"+i))).Count==10,"Preview limit10.");
        foreach(string url in new[]{"https://youtu.be/abcdefghijk","https://www.youtube.com/watch?v=abcdefghijk","https://www.youtube.com/shorts/abcdefghijk","https://www.youtube.com/live/abcdefghijk","https://www.youtube.com/embed/abcdefghijk","https://vimeo.com/123456"})
        {Check(ChatLinkPreviewService.TryVideoProvider(new Uri(url),new(){Url=url},out ChatLinkPreviewDto? provider)&&provider.Kind=="video"&&!provider.CanRemove,"Supported video provider has no removal cross.");}
        foreach(string url in new[]{"https://youtube.com.evil.invalid/watch?v=abcdefghijk","https://www.youtube.com/watch?v=bad","https://vimeo.com/not-video"})Check(!ChatLinkPreviewService.TryVideoProvider(new Uri(url),new(),out _),"Provider spoof rejected.");
        int fetches=0;List<Uri> validated=[];
        using(ChatLinkPreviewService service=new(new FakeHandler(request=>
        {
            fetches++;Check(request.Headers.Authorization is null&&!request.Headers.Contains("Cookie"),"Metadata fetch carries no account credentials.");
            return Html("<html><head><title>Fallback</title><meta property='og:title' content='Titre &amp; précis'><meta property='og:description' content='Description'><meta property='og:image' content='/cover.png'><meta property='og:video' content='https://evil.invalid/player'></head><body><script>fetch('https://evil.invalid/run')</script><iframe src='https://evil.invalid/frame'></iframe></body></html>",request);
        }),Validate))
        {
            var previews=await service.BuildAsync("https://public.example/page",None);Check(previews.Single().Title=="Titre & précis"&&previews[0].ImageUrl=="https://public.example/cover.png"&&previews[0].EmbedUrl is null&&previews[0].CanRemove,"Inert OpenGraph metadata never imports arbitrary player/script.");
            await service.BuildAsync("https://public.example/page",None);Check(fetches==1,"Metadata cache avoids duplicate remote reads.");
        }
        using(ChatLinkPreviewService redirects=new(new FakeHandler(request=>{fetches++;var response=new HttpResponseMessage(HttpStatusCode.Redirect){RequestMessage=request};response.Headers.Location=new("http://127.0.0.1/secret");return response;}),Validate))
        {int before=fetches;var fallback=await redirects.BuildAsync("https://public.example/redirect",None);Check(fetches==before+1&&fallback[0].ImageUrl is null,"Private redirect rejected before a second HTTP request.");}
        using(ChatLinkPreviewService dnsBlocked=new(new FakeHandler(_=>throw new InvalidOperationException("Must not fetch after rejected DNS.")),(_,_)=>throw new ChatOperationException("chat-invalid-link")))
        {Check((await dnsBlocked.BuildAsync("https://public.example/rebound",None))[0].Kind=="link","DNS rejection leaves an ordinary link.");}
        using(ChatLinkPreviewService large=new(new FakeHandler(request=>{var response=Html("<title>Should not parse</title>",request);response.Content.Headers.ContentLength=512*1024+1;return response;}),Validate))
        {Check((await large.BuildAsync("https://public.example/large",None))[0].Title=="public.example","Declared oversized HTML rejected.");}
        using(ChatLinkPreviewService images=new(new FakeHandler(request=>new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new ByteArrayContent(request.RequestUri!.AbsolutePath=="/good"?[137,80,78,71,13,10,26,10]:"not an image"u8.ToArray()) {Headers={ContentType=new("image/png")}}}),Validate))
        {Check(await images.FetchImageAsync("https://public.example/bad",None) is null,"Image MIME cannot override a mismatched signature.");Check((await images.FetchImageAsync("https://public.example/good",None))?.ContentType=="image/png","Recognized image fetched through bounded proxy.");}
        using(ChatLinkPreviewService media=new(new FakeHandler(request=>
        {
            Check(request.Headers.Range?.ToString()=="bytes=1-3","Byte range forwarded without credentials.");
            var response=new HttpResponseMessage(HttpStatusCode.PartialContent){RequestMessage=request,Content=new ByteArrayContent("ELL"u8.ToArray())};response.Content.Headers.ContentType=new("audio/mpeg");response.Content.Headers.ContentRange=new(1,3,5);return response;
        }),Validate))
        {
            ChatLinkedMediaRead read=await media.OpenMediaAsync("https://public.example/audio.mp3","bytes=1-3",None);await using(read.Stream){using StreamReader reader=new(read.Stream);Check(read.StatusCode==206&&read.ContentRange=="bytes 1-3/5"&&await reader.ReadToEndAsync()=="ELL","Linked media returns bounded range stream.");}
            await Error(()=>media.OpenMediaAsync("https://public.example/audio.mp3","bytes=0-1,3-4",None),"chat-invalid-range");
        }
        using(ChatLinkPreviewService hugeMedia=new(new FakeHandler(request=>{var response=new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new ByteArrayContent([])};response.Content.Headers.ContentType=new("video/mp4");response.Content.Headers.ContentLength=ChatLimits.MaximumAttachmentBytes+1;return response;}),Validate))
        {await Error(()=>hugeMedia.OpenMediaAsync("https://public.example/big.mp4",null,None),"chat-request-too-large");}
        Task Validate(Uri uri,CancellationToken token){validated.Add(uri);if(!ChatPublicUrlPolicy.TryNormalize(uri.AbsoluteUri,out _))throw new ChatOperationException("chat-invalid-link");return Task.CompletedTask;}
    }

    private static HttpResponseMessage Html(string html,HttpRequestMessage request)=>new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(html,Encoding.UTF8,"text/html")};
    private static byte[] Zip(Dictionary<string,string> entries){using MemoryStream bytes=new();using(ZipArchive zip=new(bytes,ZipArchiveMode.Create,true))foreach(var(name,value)in entries){using StreamWriter text=new(zip.CreateEntry(name).Open(),new UTF8Encoding(false));text.Write(value);}return bytes.ToArray();}
    private static async Task<ChatUploadDto> Upload(ChatAttachmentStorage storage,string name,byte[] bytes){ChatUploadDto upload=await storage.BeginAsync(1,new(name,"application/octet-stream",bytes.Length),None);await storage.AppendAsync(1,upload.Id,0,new MemoryStream(bytes),bytes.Length,None);return await storage.CompleteAsync(1,upload.Id,None);}
    private static async Task RejectContent(ChatAttachmentStorage storage,string name,byte[] bytes){ChatUploadDto upload=await storage.BeginAsync(1,new(name,"application/octet-stream",bytes.Length),None);await storage.AppendAsync(1,upload.Id,0,new MemoryStream(bytes),bytes.Length,None);await Error(()=>storage.CompleteAsync(1,upload.Id,None),"chat-file-content-mismatch");}
    private static async Task Error(Func<Task> call,string? code=null){try{await call();}catch(ChatOperationException error)when(code is null||error.Code==code){_checks++;return;}throw new InvalidOperationException("Expected media rejection: "+code);}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);_checks++;}
    private sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> handle):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(handle(request));}
    private sealed class PatternStream(long remaining):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count){int take=(int)Math.Min(remaining,count);buffer.AsSpan(offset,take).Fill((byte)'x');remaining-=take;return take;}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){token.ThrowIfCancellationRequested();int take=(int)Math.Min(remaining,buffer.Length);buffer.Span[..take].Fill((byte)'x');remaining-=take;return ValueTask.FromResult(take);}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class ThrowingStream:MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){if(_read)throw new IOException("Synthetic interrupted upload.");_read=true;buffer.Span[0]=(byte)'x';return ValueTask.FromResult(1);}
    }
}
