using AisEmulator.Api;

namespace ExecutorBalancer.Tests;

public class AisEmulatorTests
{
    [Fact]
    public void OlderAssignmentDoesNotOverwriteNewer()
    {
        var store = new AisStore();
        var order = store.CreateOrder(null, new());

        // назначения приходят с разной задержкой: сначала новое, потом старое
        Assert.True(store.ApplyAssignment(order.Id, executorId: 2, sequence: 10));
        Assert.False(store.ApplyAssignment(order.Id, executorId: 1, sequence: 7));

        Assert.Equal(2, store.GetOrder(order.Id)!.ExecutorId);
        Assert.Equal(1, store.StaleAssignments);
    }
}
