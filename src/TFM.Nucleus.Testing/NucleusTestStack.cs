using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Testing;

/// <summary>A booted Nucleus stack over one provider, schema initialised; disposing it disposes its services.</summary>
public sealed class NucleusTestStack : IDisposable
{
    private readonly ServiceProvider _services;

    internal NucleusTestStack(ServiceProvider services, IDataProvider provider, ObjectDataManager manager, CapturingLoggerProvider log)
    {
        _services = services;
        Provider = provider;
        Manager = manager;
        Log = log;
    }

    /// <summary>The initialised provider.</summary>
    public IDataProvider Provider { get; }

    /// <summary>A manager over <see cref="Provider"/>, the object a store under test is given.</summary>
    public IObjectDataManager Manager { get; }

    /// <summary>The warnings and errors the stack has logged, the schema update's among them.</summary>
    public CapturingLoggerProvider Log { get; }

    /// <summary>The stack's services, for a test that needs something else it registered.</summary>
    public IServiceProvider Services => _services;

    /// <summary>Disposes the stack's services.</summary>
    public void Dispose() => _services.Dispose();
}
