using System.Security.Cryptography;
using System.Text;
using MeshChat.Crypto;
using MeshChat.Network;
using MeshChat.Protocol;
using Xunit;

namespace MeshChat.Tests;

public class MeshTests
{
    [Fact]
    public void MeshCrypto_EncryptsDecryptsAndHidesPlaintext()
    {
        var crypto = new MeshCrypto("test-key-123");
        var wrongCrypto = new MeshCrypto("wrong-key-999");
        var secretText = "Confidential Mesh Message 42";
        var plainBytes = Encoding.UTF8.GetBytes(secretText);

        var encrypted = crypto.Encrypt(plainBytes);
        Assert.DoesNotContain(secretText, Encoding.UTF8.GetString(encrypted));

        var decrypted = crypto.Decrypt(encrypted);
        Assert.Equal(secretText, Encoding.UTF8.GetString(decrypted));

        Assert.ThrowsAny<CryptographicException>(() => wrongCrypto.Decrypt(encrypted));
    }

    [Fact]
    public async Task ThreeNodes_DiscoverEachOther_DeliverMultiHop_AndExpireByTtl()
    {
        int discoveryPort = 51500 + Random.Shared.Next(1, 500);
        const string key = "mesh-integration-secret";

        // Topology: NodeA <-> NodeB <-> NodeC (NodeA and NodeC cannot see each other directly)
        await using var nodeA = new MeshNode(new MeshNodeOptions
        {
            NodeId = "NodeA",
            DiscoveryPort = discoveryPort,
            SharedSecret = key,
            BeaconInterval = TimeSpan.FromMilliseconds(200),
            PeerTtl = TimeSpan.FromSeconds(1.2),
            AllowedPeers = ["NodeB"]
        });
        await using var nodeB = new MeshNode(new MeshNodeOptions
        {
            NodeId = "NodeB",
            DiscoveryPort = discoveryPort,
            SharedSecret = key,
            BeaconInterval = TimeSpan.FromMilliseconds(200),
            PeerTtl = TimeSpan.FromSeconds(1.2),
            AllowedPeers = ["NodeA", "NodeC"]
        });
        await using var nodeC = new MeshNode(new MeshNodeOptions
        {
            NodeId = "NodeC",
            DiscoveryPort = discoveryPort,
            SharedSecret = key,
            BeaconInterval = TimeSpan.FromMilliseconds(200),
            PeerTtl = TimeSpan.FromSeconds(1.2),
            AllowedPeers = ["NodeB"]
        });

        await nodeA.StartAsync();
        await nodeB.StartAsync();
        await nodeC.StartAsync();

        // Wait for peer discovery
        await WaitUntilAsync(() =>
            nodeA.Peers.GetActivePeers().Any(p => p.NodeId == "NodeB") &&
            nodeB.Peers.GetActivePeers().Count == 2 &&
            nodeC.Peers.GetActivePeers().Any(p => p.NodeId == "NodeB"),
            TimeSpan.FromSeconds(6));

        // Verify wire frame is encrypted and hides plaintext
        var probeEnvelope = new MeshEnvelope { From = "NodeA", To = "NodeC", Text = "SECRET_WIRE_TEXT" };
        var wireBytes = nodeA.EncodeFrame(probeEnvelope);
        Assert.DoesNotContain("SECRET_WIRE_TEXT", Encoding.UTF8.GetString(wireBytes));

        // Send message from NodeA to NodeC (must route via NodeB)
        await nodeA.SendDirectAsync("NodeC", "Hello via Mesh!");

        await WaitUntilAsync(() => nodeC.GetInbox().Any(m => m.Text == "Hello via Mesh!"), TimeSpan.FromSeconds(5));
        var delivered = nodeC.GetInbox().First(m => m.Text == "Hello via Mesh!");
        Assert.Equal("NodeA", delivered.From);
        Assert.Equal(["NodeA", "NodeB", "NodeC"], delivered.Path);

        // Verify ACK came back to NodeA
        await WaitUntilAsync(() => nodeA.AckCount >= 1, TimeSpan.FromSeconds(5));

        // Stop NodeC and verify NodeB removes it by TTL
        await nodeC.DisposeAsync();
        await WaitUntilAsync(() => !nodeB.Peers.GetActivePeers().Any(p => p.NodeId == "NodeC"), TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), $"Condition not met within {timeout.TotalSeconds}s.");
    }
}
