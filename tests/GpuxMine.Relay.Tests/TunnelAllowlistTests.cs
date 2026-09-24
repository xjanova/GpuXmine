using System.Text;
using GpuxMine.Protocol;

namespace GpuxMine.Relay.Tests;

public class TunnelAllowlistTests
{
    [Theory]
    [InlineData("GET", "/object_info")]
    [InlineData("GET", "/object_info/CheckpointLoaderSimple")]
    [InlineData("GET", "/object_info/Image Save")]
    [InlineData("POST", "/prompt")]
    [InlineData("GET", "/history/0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11")]
    [InlineData("GET", "/history")]
    [InlineData("GET", "/history?max_items=64")]
    [InlineData("GET", "/queue")]
    [InlineData("GET", "/view?filename=ComfyUI_00001_.png&subfolder=&type=output")]
    [InlineData("GET", "/view?filename=x.png&type=temp&preview=webp")]
    [InlineData("POST", "/upload/image")]
    [InlineData("POST", "/interrupt")]
    [InlineData("GET", "/aixman/ready")]
    [InlineData("GET", "/aixman/progress")]
    [InlineData("POST", "/aixman/purge")]
    public void What_aixman_uses_is_allowed(string method, string path)
        => Assert.Equal(TunnelVerdict.Allowed, TunnelAllowlist.Check(method, path));

    [Theory]
    // ComfyUI-Manager and friends: installing a custom node is running code on the owner's PC.
    [InlineData("POST", "/customnode/install")]
    [InlineData("GET", "/manager/reboot")]
    [InlineData("POST", "/api/manager/queue/install")]
    [InlineData("POST", "/userdata/comfy.settings.json")]
    [InlineData("GET", "/userdata?dir=workflows")]
    [InlineData("POST", "/free")]
    [InlineData("GET", "/system_stats")]
    [InlineData("GET", "/extensions")]
    [InlineData("POST", "/upload/mask")]
    [InlineData("GET", "/internal/logs")]
    // Right path, wrong method.
    [InlineData("DELETE", "/queue")]
    [InlineData("POST", "/queue")]
    [InlineData("GET", "/prompt")]
    [InlineData("PUT", "/prompt")]
    [InlineData("HEAD", "/view?filename=x.png")]
    [InlineData("GET", "/interrupt")]
    [InlineData("GET", "/upload/image")]
    // Walking out of the named route.
    [InlineData("GET", "/object_info/../userdata/x")]
    [InlineData("GET", "/object_info/..%2F..%2Fuserdata")]
    [InlineData("GET", "/object_info/a%2Fb")]
    [InlineData("GET", "/history/..")]
    [InlineData("GET", "/aixman/../customnode/install")]
    [InlineData("GET", "/aixman")]
    [InlineData("GET", "/object_info/")]
    [InlineData("GET", "//object_info")]
    [InlineData("GET", "/object_info\\x")]
    [InlineData("GET", "/history/abc#/../x")]
    [InlineData("GET", "/object_info/a#b")]
    [InlineData("GET", "/history?clear=true")]
    [InlineData("GET", "")]
    [InlineData("GET", "object_info")]
    public void Everything_else_is_denied(string method, string path)
        => Assert.Equal(TunnelVerdict.Denied, TunnelAllowlist.Check(method, path));

    [Theory]
    [InlineData("/view?filename=../../../../etc/passwd")]
    [InlineData("/view?filename=..%2F..%2Fsecret.png")]
    [InlineData("/view?filename=x.png&subfolder=..")]
    [InlineData("/view?filename=x.png&subfolder=%2Fetc")]
    [InlineData("/view?filename=C:%5CWindows%5Cwin.ini")]
    [InlineData("/view?filename=C:/Windows/win.ini")]
    [InlineData("/view?filename=x.png&type=models")]
    [InlineData("/view?filename=x.png&type=../output")]
    [InlineData("/view?subfolder=output")]
    [InlineData("/view")]
    [InlineData("/view?filename=ok.png&filename=../../bad")]
    public void View_refuses_names_that_leave_the_three_folders(string path)
        => Assert.Equal(TunnelVerdict.Denied, TunnelAllowlist.Check("GET", path));

    [Fact]
    public void Posting_history_needs_its_body_read()
        => Assert.Equal(TunnelVerdict.NeedsBodyCheck, TunnelAllowlist.Check("POST", "/history"));

    [Theory]
    [InlineData("""{"delete":["0f8c4f7e-3b1a-4c2e-9d3b-6b1f0a2c9e11"]}""", true)]
    [InlineData("""{"delete":[]}""", true)]
    [InlineData("""{"clear":false}""", true)]
    [InlineData("""{"delete":["a"],"clear":false}""", true)]
    [InlineData("""{}""", true)]
    [InlineData("""{"clear":true}""", false)]
    [InlineData("""{"delete":["a"],"clear":true}""", false)]
    [InlineData("""{"delete":"a"}""", false)]
    [InlineData("""{"delete":[1]}""", false)]
    [InlineData("""{"delete":["../x"]}""", false)]
    [InlineData("""{"wipe":true}""", false)]
    [InlineData("""[]""", false)]
    [InlineData("""not json""", false)]
    [InlineData("", false)]
    public void History_post_may_delete_but_never_clear(string body, bool allowed)
        => Assert.Equal(allowed, TunnelAllowlist.IsAllowed("POST", "/history", Encoding.UTF8.GetBytes(body)));
}
