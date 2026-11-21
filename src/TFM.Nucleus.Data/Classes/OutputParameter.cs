using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A SET term whose stored value the write reads back: the renderer asks the server for it
    /// (OUTPUT on SQL Server, RETURNING on PostgreSQL), the context hands it to
    /// <see cref="SetReceivedOutput"/>, and <see cref="OutputReceived"/> passes it on, normally to
    /// the entity the term writes.
    /// </summary>
    public class OutputParameter : Parameter, IQueryOutputParameter
    {
        #region IQueryOutputParameter Implementation
[Obsolete("Use AsOutputParameter.")]
        public new IQueryOutputParameter castAsOutputParameter() => this;

        /// <inheritdoc cref="IQueryParameter.AsOutputParameter"/>
        public override IQueryOutputParameter AsOutputParameter() => this;
        #endregion

        /// <summary>The value the server returned, once the write has run.</summary>
        public object? ReceivedObject { get; set; }

        public OutputParameter(string name, object value, EventHandler<IOutputReceivedEventArgs>? onOutputReceived) : base(true, name, value)
        {
            if (onOutputReceived != null)
                OutputReceived += onOutputReceived;
        }

        /// <summary>A term on <paramref name="column"/> that writes <paramref name="value"/> and reads the stored value back.</summary>
        public OutputParameter(IDataColumnSchema column, object? value, EventHandler<IOutputReceivedEventArgs>? onOutputReceived) : base(column, value, true)
        {
            if (onOutputReceived != null)
                OutputReceived += onOutputReceived;
        }

        /// <summary>
        /// Records the value the server returned and raises <see cref="OutputReceived"/>, naming
        /// the property the term's column stores, or the column when the term has no schema.
        /// </summary>
        public void SetReceivedOutput(object? value)
        {
            ReceivedObject = value;
            OutputReceived?.Invoke(this, new OutputReceivedEventArgs() { PropertyName = ColumnSchema?.PropertySchema.Name ?? Name, Value = value });
        }
        public event EventHandler<IOutputReceivedEventArgs>? OutputReceived;

        public delegate void OutputReceivedEventHandler(IQueryOutputParameter sender, IOutputReceivedEventArgs args);
        public class OutputReceivedEventArgs : EventArgs, IOutputReceivedEventArgs
        {
            public string? PropertyName { get; set; }
            public object? Value { get; set; }
        }
    }
}
