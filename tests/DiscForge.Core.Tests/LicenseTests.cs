using System;
using DiscForge.Core.Licensing;
using Xunit;

namespace DiscForge.Core.Tests;

public class LicenseTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (byte[] pub, byte[] priv) Keys() => License.GenerateKeyPair();

    private static LicenseInfo Info(DateTime? expires = null, string? machine = null) => new()
    {
        Name = "MaTRIX TeAm QA",
        Edition = "Pro",
        IssuedUtc = Now,
        ExpiresUtc = expires,
        MachineId = machine,
    };

    [Fact]
    public void A_signed_licence_validates_against_its_public_key()
    {
        var (pub, priv) = Keys();
        string key = License.Issue(Info(), priv);

        var r = License.Validate(key, pub, currentMachineId: null, Now);

        Assert.True(r.IsValid);
        Assert.Equal(LicenseState.Valid, r.State);
        Assert.Equal("MaTRIX TeAm QA", r.Info!.Name);
        Assert.Equal("Pro", r.Info.Edition);
    }

    [Fact]
    public void A_key_signed_by_a_different_private_key_is_rejected()
    {
        var (_, priv) = Keys();
        var (otherPub, _) = Keys();                       // different key pair
        string key = License.Issue(Info(), priv);

        var r = License.Validate(key, otherPub, null, Now);
        Assert.Equal(LicenseState.BadSignature, r.State);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void A_tampered_key_fails_the_signature_check()
    {
        var (pub, priv) = Keys();
        string key = License.Issue(Info(), priv);

        // Flip a character in the payload portion (before the '.').
        int dot = key.IndexOf('.');
        var chars = key.ToCharArray();
        chars[2] = chars[2] == 'A' ? 'B' : 'A';   // a payload character well before the '.'
        Assert.True(2 < dot);
        var r = License.Validate(new string(chars), pub, null, Now);

        Assert.NotEqual(LicenseState.Valid, r.State);
    }

    [Fact]
    public void An_expired_licence_is_reported_expired()
    {
        var (pub, priv) = Keys();
        string key = License.Issue(Info(expires: Now.AddDays(-1)), priv);

        var r = License.Validate(key, pub, null, Now);
        Assert.Equal(LicenseState.Expired, r.State);
        Assert.NotNull(r.Info);                            // contents still decoded
    }

    [Fact]
    public void A_not_yet_expired_licence_is_valid()
    {
        var (pub, priv) = Keys();
        string key = License.Issue(Info(expires: Now.AddDays(30)), priv);
        Assert.True(License.Validate(key, pub, null, Now).IsValid);
    }

    [Fact]
    public void A_machine_locked_licence_only_validates_on_that_machine()
    {
        var (pub, priv) = Keys();
        string bound = MachineId.FromRaw("PC-ALPHA-GUID");
        string key = License.Issue(Info(machine: bound), priv);

        Assert.True(License.Validate(key, pub, bound, Now).IsValid);
        Assert.Equal(LicenseState.WrongMachine,
            License.Validate(key, pub, MachineId.FromRaw("PC-BETA-GUID"), Now).State);
        // With no machine supplied, the lock is not enforced (inspection mode).
        Assert.True(License.Validate(key, pub, null, Now).IsValid);
    }

    [Fact]
    public void Missing_and_malformed_keys_are_distinguished()
    {
        var (pub, _) = Keys();
        Assert.Equal(LicenseState.Missing, License.Validate("", pub, null, Now).State);
        Assert.Equal(LicenseState.Missing, License.Validate(null, pub, null, Now).State);
        Assert.Equal(LicenseState.Malformed, License.Validate("not-a-key", pub, null, Now).State);
        Assert.Equal(LicenseState.Malformed, License.Validate("no-dot-here", pub, null, Now).State);
    }

    [Fact]
    public void Machine_id_is_deterministic_stable_and_opaque()
    {
        string a = MachineId.FromRaw("some-machine-guid");
        string b = MachineId.FromRaw("some-machine-guid");
        Assert.Equal(a, b);
        Assert.NotEqual(a, MachineId.FromRaw("other-guid"));
        Assert.DoesNotContain("some-machine-guid", a);     // not reversible
        Assert.Equal(19, a.Length);                         // XXXX-XXXX-XXXX-XXXX
        // Boolean computed first so the xUnit analyzer doesn't flag this as xUnit2008
        // (it suggests Assert.Matches, which our custom Harness doesn't implement).
        bool wellFormed = System.Text.RegularExpressions.Regex.IsMatch(
            a, "^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$");
        Assert.True(wellFormed);
    }

    [Fact]
    public void The_embedded_placeholder_public_key_is_a_valid_p256_key()
    {
        // It must parse (so the app runs). A licence signed by some other private key
        // reaches the signature check and fails there — proving the embedded key imported,
        // and that the shipped app is "unlicensed by default" until the vendor swaps its key.
        var (_, priv) = Keys();
        string key = License.Issue(Info(), priv);
        var r = License.Validate(key, LicenseConfig.PublicSpki, null, Now);
        Assert.Equal(LicenseState.BadSignature, r.State);
    }
}
