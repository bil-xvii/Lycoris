using Monolith.Core.Devices;
using Monolith.Modules.Devices.Displays;

namespace Monolith.Tests.Devices;

/// <summary>
/// The «--display-guard» command line: built by Lycoris, parsed strictly by the guard process (anything malformed → the
/// guard does nothing). Pure: no process is started, no display is touched.
/// </summary>
public sealed class DisplayGuardArgsTests
{
    private const string Path = @"\\?\DISPLAY#ACR1132#5&30ca09c6&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static string[] Mode(DateTime? deadline = null) =>
        DisplayWriter.BuildGuardArgs(Id, deadline ?? Now.AddSeconds(35), 4242, new DisplayGuardAction(DisplayGuardKind.Mode, Path, false));

    private static string[] Hdr(bool original = true, string path = Path) =>
        DisplayWriter.BuildGuardArgs(Id, Now.AddSeconds(35), 4242, new DisplayGuardAction(DisplayGuardKind.Hdr, path, original));

    private static bool Parse(string[] args, out GuardArgs? parsed) => DisplayWriter.TryParseGuardArgs(args, Now, out parsed);

    [Fact]
    public void Mode_and_hdr_round_trip()
    {
        var mode = Mode();
        Assert.Equal(["--display-guard", Id.ToString("N"), Now.AddSeconds(35).Ticks.ToString(), "4242", "mode"], mode);
        Assert.True(Parse(mode, out var m));
        Assert.Equal(new GuardArgs(Id, Now.AddSeconds(35), 4242, DisplayGuardKind.Mode, false, null), m);

        foreach (var original in new[] { true, false })
        {
            Assert.True(Parse(Hdr(original), out var h));
            Assert.Equal(DisplayGuardKind.Hdr, h!.Kind);
            Assert.Equal(original, h.OriginalHdr);
            Assert.Equal(Path, h.MonitorPath);
            Assert.Equal(Id, h.RecordId);
            Assert.Equal(4242, h.ParentPid);
            Assert.Equal(DateTimeKind.Utc, h.DeadlineUtc.Kind);
        }
        Assert.Equal(DisplayGuardProcess.Switch, Hdr()[0]);
        Assert.Equal(7, Hdr().Length);
    }

    [Fact]
    public void Wrong_arity_is_refused()
    {
        Assert.False(Parse(Mode()[..4], out _));
        Assert.False(Parse([.. Mode(), "extra"], out _));
        Assert.False(Parse(Hdr()[..6], out _));
        Assert.False(Parse([.. Hdr(), "extra"], out _));
        Assert.False(Parse([], out _));
        Assert.False(DisplayWriter.TryParseGuardArgs(null, Now, out _));
        var wrongSwitch = Mode();
        wrongSwitch[0] = "--display";
        Assert.False(Parse(wrongSwitch, out _));
    }

    [Theory]
    [InlineData("not-a-guid-at-all-not-a-guid-xx")]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f")]   // 31 chars
    [InlineData("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")] // dashes: not the N format
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1fz")]
    public void A_non_hex_id_is_refused(string id)
    {
        var args = Mode();
        args[1] = id;
        Assert.False(Parse(args, out _));
    }

    [Fact]
    public void A_deadline_in_the_past_or_more_than_two_minutes_ahead_is_refused()
    {
        Assert.False(Parse(Mode(Now.AddSeconds(-1)), out _));
        Assert.False(Parse(Mode(Now.AddMinutes(2).AddSeconds(1)), out _));
        Assert.True(Parse(Mode(Now.AddMinutes(2)), out _));
        var args = Mode();
        args[2] = "-5";
        Assert.False(Parse(args, out _));
        args[2] = "12abc";
        Assert.False(Parse(args, out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public void A_bad_pid_is_refused(string pid)
    {
        var args = Mode();
        args[3] = pid;
        Assert.False(Parse(args, out _));
    }

    [Fact]
    public void An_unknown_kind_or_hdr_flag_is_refused()
    {
        var kind = Mode();
        kind[4] = "MODE";
        Assert.False(Parse(kind, out _));
        kind[4] = "revert";
        Assert.False(Parse(kind, out _));

        var flag = Hdr();
        flag[5] = "2";
        Assert.False(Parse(flag, out _));
        flag[5] = "true";
        Assert.False(Parse(flag, out _));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1")]
    [InlineData("\\\\?\\DISPLAY#ACR1132#5&30ca\"09c6#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\?\DISPLAY#ACR1132#5&30ca09c6&0&UID4355#{00000000-0000-0000-0000-000000000000}")]
    public void A_path_failing_the_monitor_path_check_is_refused(string path)
    {
        Assert.False(Parse(Hdr(path: path), out _));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("XFw_XERJU1BMQVk=")]   // padding is not base64url
    [InlineData("XFw/XERJU1BMQVk")]    // '/' is not base64url
    [InlineData("")]
    [InlineData("A")]                  // impossible length
    public void Bad_base64_is_refused(string encoded)
    {
        var args = Hdr();
        args[6] = encoded;
        Assert.False(Parse(args, out _));
    }

    [Fact]
    public void Base64url_is_canonical()
    {
        var encoded = DisplayWriter.Base64Url(Path);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(Path, DisplayWriter.FromBase64Url(encoded));

        // The same bytes with non-zero padding bits in the last character: decodable, but not the canonical form.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        Assert.NotEqual(0, encoded.Length % 4);
        var nonCanonical = encoded[..^1] + alphabet[alphabet.IndexOf(encoded[^1]) + 1];
        Assert.Null(DisplayWriter.FromBase64Url(nonCanonical));
        // Extra data decodes to another string, which the monitor-path check then refuses.
        Assert.NotEqual(Path, DisplayWriter.FromBase64Url(encoded + "AA"));
        var args = Hdr();
        args[6] = encoded + "AA";
        Assert.False(Parse(args, out _));
    }

    [Fact]
    public void Event_names_carry_the_record_id()
    {
        Assert.Equal($@"Local\MONOLITH-DisplayKeep-{Id:N}", DisplayWriter.KeepEventName(Id));
        Assert.Equal($@"Local\MONOLITH-DisplayArmed-{Id:N}", DisplayWriter.ArmedEventName(Id));
    }
}
