using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Networking;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The networking that used to be a stub: a session, the frames it carries, and the wire the
/// browser engine has to agree with.
/// </summary>
public class NetworkingTests : IDisposable
{
    // The singleton is process-wide, so each test disposes whatever it started. Leaving one
    // running makes the next test throw "already running" from its constructor, which reads
    // like a bug in the manager rather than a leak in the test above it.
    public void Dispose() => NetworkManager.Instance?.Dispose();

    private static void Pump(NetworkManager manager, int frames = 4)
    {
        for (int i = 0; i < frames; i++) manager.Tick(1f / 60f);
    }

    // -------------------------------------------------------------------------
    // The wire
    // -------------------------------------------------------------------------

    [Fact]
    public void EveryFieldSurvivesAWriteAndARead()
    {
        var frame = new NetWriter(NetMessage.Spawn)
            .UInt(7).String("Player").Int(-1).Bytes(new byte[] { 1, 2, 3 })
            .Bool(true).Float(1.5f).Double(2.25).ToArray();

        var reader = new NetReader(frame);
        Assert.Equal(NetMessage.Spawn, reader.Byte());
        Assert.Equal(7u, reader.UInt());
        Assert.Equal("Player", reader.String());
        Assert.Equal(-1, reader.Int());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Bytes());
        Assert.True(reader.Bool());
        Assert.Equal(1.5f, reader.Float());
        Assert.Equal(2.25, reader.Double());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void NumbersGoOutLittleEndianWhateverTheMachineIs()
    {
        // The browser reads these with DataView, which defaults to big-endian. If the writer
        // ever followed the host's endianness instead of saying so explicitly, the two engines
        // would agree on x86 and silently disagree everywhere else.
        byte[] frame = new NetWriter().UInt(0x01020304).ToArray();
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, frame);
    }

    [Fact]
    public void AStringIsPrefixedWithItsByteLengthNotItsCharacterCount()
    {
        // A four-byte emoji is one JavaScript string of length two and one UTF-8 run of four
        // bytes. Prefixing anything but the byte count desynchronises the whole frame after it.
        byte[] frame = new NetWriter().String("é").ToArray();
        Assert.Equal(new byte[] { 2, 0, 0xC3, 0xA9 }, frame);
    }

    [Fact]
    public void ATruncatedFrameThrowsRatherThanReadingRubbish()
    {
        // These bytes come off a socket. A short read has to be a caught exception, not an
        // undefined value that poisons the game state ten calls later.
        // A ref struct cannot be captured by a lambda, so the read happens in a local method.
        static void ReadPastTheEnd()
        {
            var reader = new NetReader(new byte[] { 1, 2 });
            reader.Int();
        }

        Assert.Throws<NetProtocolException>(ReadPastTheEnd);
    }

    // -------------------------------------------------------------------------
    // Sessions
    // -------------------------------------------------------------------------

    [Fact]
    public void ASoloSessionIsAFullSessionWithNoSocket()
    {
        // A multiplayer game played alone must not be a different game: solo has to go through
        // the same handshake, so "works alone, breaks in a lobby" cannot happen.
        using var manager = new NetworkManager { PlayerName = "Solo" };
        manager.StartSolo();
        Pump(manager);

        Assert.True(manager.IsServer);
        Assert.True(manager.IsClient);
        Assert.True(manager.IsHost);
        Assert.True(manager.IsConnected);
        Assert.Equal(NetTransportKind.Loopback, manager.Transport);
        Assert.Equal("Solo", manager.Players[0]);
    }

    [Fact]
    public void AClientIsGivenAnIdAndTheRosterItJoined()
    {
        using var server = new NetworkManager { PlayerName = "Host" };
        server.StartSolo();
        Pump(server);

        // The loopback client half introduced itself, so the server has two seats: its own,
        // and the one the handshake created.
        Assert.Contains(server.Players, p => p.Value == "Host");
        Assert.Single(server.ConnectedClientIds);
    }

    [Fact]
    public void AMessageReachesTheOtherEndWithTheSenderTheServerAssigned()
    {
        using var manager = new NetworkManager();
        manager.StartSolo();
        Pump(manager);

        var received = new List<(int Sender, string Type, string? Payload)>();
        manager.OnMessage += (sender, type, payload) =>
            received.Add((sender, type, payload?["hp"]?.GetValue<int>().ToString()));

        manager.SendMessageToAll("hit", new JsonObject { ["hp"] = 42 });
        Pump(manager);

        var message = Assert.Single(received);
        Assert.Equal("hit", message.Type);
        Assert.Equal("42", message.Payload);
    }

    [Fact]
    public void AMessageWithNoPayloadStillArrives()
    {
        // The templates send bare signals -- "startGame", "ready" -- with nothing attached.
        using var manager = new NetworkManager();
        manager.StartSolo();
        Pump(manager);

        string? seen = null;
        manager.OnMessage += (_, type, payload) => { seen = type; Assert.Null(payload); };

        manager.SendMessageToAll("startGame");
        Pump(manager);

        Assert.Equal("startGame", seen);
    }

    [Fact]
    public void StartingTwiceIsRefusedRatherThanLeakingTheFirstSession()
    {
        using var manager = new NetworkManager();
        manager.StartSolo();
        Assert.Throws<InvalidOperationException>(() => manager.StartSolo());
    }

    [Fact]
    public void DisconnectingClearsEverythingSoTheManagerCanBeReused()
    {
        using var manager = new NetworkManager();
        manager.StartSolo();
        Pump(manager);
        manager.Disconnect();

        Assert.False(manager.IsRunning);
        Assert.False(manager.IsServer);
        Assert.Empty(manager.Players);

        // And it starts again cleanly -- returning to a menu and hosting a second match is
        // the most ordinary thing a player does.
        manager.StartSolo();
        Pump(manager);
        Assert.True(manager.IsConnected);
    }

    [Fact]
    public void ANetworkIdIsOnlyAllocatedByTheServer()
    {
        using var manager = new NetworkManager();
        manager.StartSolo();
        Pump(manager);

        uint first  = manager.GetType()
            .GetMethod("AllocateNetworkId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(manager, null) is uint id ? id : 0;
        Assert.True(first > 0);
    }

    // -------------------------------------------------------------------------
    // Replication
    // -------------------------------------------------------------------------

    private sealed class Health : Component
    {
        [Replicated] public int Hp = 100;
        [Replicated] public Vector2 Aim;
        public int Ignored = 7;
    }

    [Fact]
    public void OnlyChangedMembersAreSentAfterTheFirstSnapshot()
    {
        // The point of a dirty check: a full snapshot every tick is the difference between a
        // game that plays over a phone connection and one that does not.
        var scene = new Engine.Core.Scene("replicate");
        try
        {
            var actor  = scene.AddActor(new Actor("Bot"));
            var health = actor.AddComponent<Health>();
            var netObj = actor.AddComponent<NetworkObject>();
            scene.FlushPendingActors();

            byte[] first = netObj.CollectLocalState(forceAll: true);
            Assert.True(MemberCount(first) >= 2);

            Assert.Equal(0, MemberCount(netObj.CollectLocalState()));

            health.Hp = 50;
            Assert.Equal(1, MemberCount(netObj.CollectLocalState()));
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AStateBlobAppliesOntoAnotherActor()
    {
        var scene = new Engine.Core.Scene("apply");
        try
        {
            var source = scene.AddActor(new Actor("Source"));
            var sourceHealth = source.AddComponent<Health>();
            var sourceNet = source.AddComponent<NetworkObject>();

            var target = scene.AddActor(new Actor("Target"));
            var targetHealth = target.AddComponent<Health>();
            var targetNet = target.AddComponent<NetworkObject>();
            scene.FlushPendingActors();

            sourceHealth.Hp  = 33;
            sourceHealth.Aim = new Vector2(4, -5);
            targetNet.ApplyRemoteState(sourceNet.CollectLocalState(forceAll: true));

            Assert.Equal(33, targetHealth.Hp);
            Assert.Equal(new Vector2(4, -5), targetHealth.Aim);
            Assert.Equal(7, targetHealth.Ignored);   // not marked, not sent
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AMemberTheOtherBuildDoesNotHaveIsSteppedOverRatherThanBreakingTheBlob()
    {
        // What lets an older client stay in a session with a newer server. Without the length
        // in the tag, one unknown member would desynchronise every member after it.
        var scene = new Engine.Core.Scene("skip");
        try
        {
            var target = scene.AddActor(new Actor("Target"));
            var health = target.AddComponent<Health>();
            var netObj = target.AddComponent<NetworkObject>();
            scene.FlushPendingActors();

            // Two members: one this build has never heard of, then one it has.
            var blob = new NetWriter();
            blob.Byte(2).Byte(0);
            blob.String("Stamina").Byte(2).Int(9);          // tag 2 = Int32
            blob.String("Hp").Byte(2).Int(64);
            netObj.ApplyRemoteState(blob.ToArray());

            Assert.Equal(64, health.Hp);
        }
        finally { scene.Destroy(); }
    }

    private static int MemberCount(byte[] blob) => blob.Length < 2 ? 0 : blob[0] | (blob[1] << 8);

    // -------------------------------------------------------------------------
    // The script hook
    // -------------------------------------------------------------------------

    [Fact]
    public void AScriptsOnNetworkMessageHookReceivesTheMessage()
    {
        // The templates were written against a hook that did not exist. It does now, and it
        // has to fire for a script that joins a session after the script started -- a lobby
        // calls Network.startServer from onUpdate, which is after every onStart has run.
        using var project = new ScriptProject();
        var scene = new Engine.Core.Scene("hook");
        try
        {
            var actor  = scene.AddActor(new Actor("Listener"));
            var script = actor.AddComponent<Engine.Scripting.ScriptComponent>();
            script.ScriptPath = project.Write("Listener.js",
                "var seen = \"\", from = -1;\n" +
                "function onNetworkMessage(type, data, sender) {\n" +
                "    seen = type + \":\" + (data ? data.hp : \"-\");\n" +
                "    from = sender;\n" +
                "}\n");
            scene.FlushPendingActors();
            scene.Update(1f / 60f);

            using var manager = new NetworkManager();
            manager.StartSolo();
            Pump(manager);

            // One frame so the component notices the session that started after it did.
            scene.Update(1f / 60f);

            manager.SendMessageToAll("hit", new JsonObject { ["hp"] = 7 });
            Pump(manager);

            Assert.Equal("hit:7", script.Runtime!.Evaluate("seen")!.ToString());
            Assert.Equal("0", script.Runtime.Evaluate("String(from)")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    // -------------------------------------------------------------------------
    // Parity with the browser engine
    // -------------------------------------------------------------------------

    [Fact]
    public void TheBrowserEngineDeclaresTheSameMessageIdsAndProtocolVersion()
    {
        // The two implementations are the wire. A number that differs by one here is a session
        // that connects, handshakes, and then silently mis-decodes every frame -- the failure
        // that is impossible to diagnose from a bug report, so it is a test instead.
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string js = File.ReadAllText(Path.Combine(repo!.Root, "html5", "src", "net", "protocol.js"));

        foreach (var field in typeof(NetMessage).GetFields())
        {
            byte value = (byte)field.GetRawConstantValue()!;
            var match = Regex.Match(js, $@"\b{field.Name}:\s*0x([0-9A-Fa-f]+)");
            Assert.True(match.Success, $"html5/src/net/protocol.js does not declare NetMessage.{field.Name}");
            Assert.Equal(value, Convert.ToByte(match.Groups[1].Value, 16));
        }

        var version = Regex.Match(js, @"NET_PROTOCOL_VERSION\s*=\s*(\d+)");
        Assert.True(version.Success, "html5/src/net/protocol.js does not declare NET_PROTOCOL_VERSION");
        Assert.Equal(NetProtocolVersion.Current, int.Parse(version.Groups[1].Value));
    }

    [Fact]
    public void TheBrowserEngineDeclaresTheSameValueTags()
    {
        // The tags inside a replicated state blob. They are private to each engine and have to
        // match anyway, because the blob crosses between them untouched.
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string js = File.ReadAllText(Path.Combine(repo!.Root, "html5", "src", "net", "NetworkObject.js"));
        var tagType = typeof(NetworkObject).GetNestedType("ValueTag", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(tagType);

        foreach (var name in Enum.GetNames(tagType!))
        {
            int value = (int)Convert.ChangeType(Enum.Parse(tagType, name), typeof(int));
            var match = Regex.Match(js, $@"\b{name}:\s*(\d+)");
            Assert.True(match.Success, $"html5/src/net/NetworkObject.js does not declare the {name} tag");
            Assert.Equal(value, int.Parse(match.Groups[1].Value));
        }
    }
}
