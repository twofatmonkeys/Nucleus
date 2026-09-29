using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using TFM.Nucleus.Abstractions.Interfaces;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public interface ICoreObject
    {
        public IDataProvider? DataProvider { get; set; }
        Guid ID { get; }
        bool IsNew { get; }
        bool IsReadOnly { get; }
        DateTimeOffset Timestamp { get; }
        protected void OnOutputParameterReceived(object sender, IOutputReceivedEventArgs args);
        public void ChildPropertyChanged(object sender, PropertyChangedEventArgs e);
        protected bool HasChangedLinkedObjects(out ICoreObject[]? objects);
        //static abstract IEnumerable<T> BindDataSet<T>(IDataSet dataSet) where T : ICoreObject, new();
        public Dictionary<string, object?> ChangedProperties { get; }
        public void CommitChanges(ILogger? logger = null);
        public T? GetPropertyValue<T>(string propertyName);
        public object? GetPropertyValue(string propertyName);
        public void BindValue(string property, object? value);
        public IObjectSchema ObjectSchema { get; }
        //protected ISqlSchema SqlSchema { get; }
        public IEnumerable<INucleusChildCollection> NucleusChildCollections { get; }


        public Guid[] GetLinkedIDsForLogging()
        {
            return ObjectSchema.GetLinkedIDsForLogging(this);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public interface ICoreObject<T> : ICoreObject
    {
        //public static abstract IEnumerable<T> BindDataSet(IDataSet dataSet);
    }

    public interface ICoreChildObject<TParent> : ICoreObject
    {
        public TParent Parent { get; }
        public void BindParent(TParent parent);

        /// <summary>
        /// Binds the parent of a child read from storage as its stored value: no change is
        /// staged and no event raised, so a freshly loaded child is not dirty. Nucleus calls this
        /// while materialising; application code adds children through the collection.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public void BindLoadedParent(TParent parent) => BindParent(parent);

    }

    public interface ICoreChildObject<TChild, TParent> : ICoreObject<TChild>, ICoreChildObject<TParent>
    {
    }
}
