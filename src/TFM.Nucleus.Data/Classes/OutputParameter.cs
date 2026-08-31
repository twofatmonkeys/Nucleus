using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public class OutputParameter : Parameter, IQueryOutputParameter
    {
        #region IQueryOutputParameter Implementation
        public new IQueryOutputParameter castAsOutputParameter() => this;
        #endregion

        public object? ReceivedObject { get; set; }

        public OutputParameter(string name, object value, EventHandler<IOutputReceivedEventArgs>? onOutputReceived) : base(true, name, value)
        {
            if (onOutputReceived != null)
                OutputReceived += onOutputReceived;
        }

        public void SetReceivedOutput(object value)
        {
            OutputReceived?.Invoke(this, new OutputReceivedEventArgs() { PropertyName = Name, Value = value });
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
