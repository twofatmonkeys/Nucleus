
namespace TFM.Nucleus.Data
{
    public interface IQueryOutputParameter : IQueryParameter
    {
        public object? ReceivedObject { get; set; }
        public event EventHandler<IOutputReceivedEventArgs>? OutputReceived;
    }

    public interface IOutputReceivedEventArgs 
    {
        public string? PropertyName { get; }
        public object? Value { get; }
    }
}
