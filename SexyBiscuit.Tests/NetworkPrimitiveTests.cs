using System.Reflection;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Networking;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The two things a networked game needs from the engine that a game cannot add for itself.
/// </summary>
public class NetworkPrimitiveTests
{
    [Theory]
    [InlineData(nameof(PlayerState.PlayerName))]
    [InlineData(nameof(PlayerState.PlayerId))]
    [InlineData(nameof(PlayerState.Score))]
    [InlineData(nameof(PlayerState.TeamId))]
    [InlineData(nameof(PlayerState.PingMs))]
    [InlineData(nameof(PlayerState.IsBot))]
    public void EveryScoreboardFieldIsMarkedForReplication(string property)
    {
        // None of them were, so a client that joined saw an empty scoreboard. A game cannot fix
        // this by subclassing: shadowing a property sends it twice under two names while GameState
        // keeps reading the base. This also catches the next field somebody adds.
        var member = typeof(PlayerState).GetProperty(property, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(member);
        Assert.NotNull(member!.GetCustomAttribute<ReplicatedAttribute>());
    }

    [Fact]
    public void InterestCullingMeasuresDistanceInThreeDimensions()
    {
        // It read the 2D transform, which a 3D actor never moves, so every object reported the
        // origin and nothing was ever culled. It failed safe, so this cost bandwidth rather than
        // correctness -- but the interest radius was decorative.
        var scene = new Engine.Core.Scene("interest");
        try
        {
            var actor = scene.AddActor(new Actor("Far"));
            actor.AddComponent<NetworkObject>();
            actor.AddComponent<Transform3D>().Position = new Vector3(0f, 0f, 500f);
            scene.FlushPendingActors();

            var position = ReplicationSystem.PositionOf(actor.GetComponent<NetworkObject>()!);

            Assert.Equal(500f, position.Z, 3);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AnActorWithoutA3DTransformSitsOnTheGroundPlane()
    {
        var scene = new Engine.Core.Scene("interest2d");
        try
        {
            var actor = scene.AddActor(new Actor("Flat"));
            actor.AddComponent<NetworkObject>();
            actor.Transform.LocalPosition = new Vector2(3f, 4f);
            scene.FlushPendingActors();

            var position = ReplicationSystem.PositionOf(actor.GetComponent<NetworkObject>()!);

            Assert.Equal(new Vector3(3f, 4f, 0f), position);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void ASpawnKeyFallsBackToTheActorsName()
    {
        // The spawn packet keyed on the display name, which is neither unique nor stable.
        var scene = new Engine.Core.Scene("spawnkey");
        try
        {
            var actor = scene.AddActor(new Actor("Crate"));
            var net   = actor.AddComponent<NetworkObject>();
            scene.FlushPendingActors();

            Assert.Equal("Crate", net.SpawnKeyOrName);

            net.SpawnKey = "prefab:crate";
            Assert.Equal("prefab:crate", net.SpawnKeyOrName);
        }
        finally { scene.Destroy(); }
    }
}
