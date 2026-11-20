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

        /// <summary>
        /// Checks the object before a save and gets it ready to write: its rules, and those of the
        /// new and changed children and forceSave links the save reaches, then its save hooks.
        /// Throws to reject the save. Nucleus calls this once per object being saved, on every save
        /// path, before anything is built or written; application code overrides
        /// <c>CoreObject.Validate</c> and <c>CoreObject.PreSave</c> rather than calling this.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void PrepareForSave();
        public T? GetPropertyValue<T>(string propertyName);
        public object? GetPropertyValue(string propertyName);
        public void BindValue(string property, object? value);

        /// <summary>
        /// Runs the entity's load hook once a load has finished it: its columns and parent bound,
        /// its loaded children attached (their hooks run first), and any excluded collection
        /// marked unloaded. Nucleus calls this once at the end of every load; application code
        /// overrides <c>CoreObject.OnAfterLoadFromDB</c> rather than calling this.
        /// </summary>
        public void OnAfterLoadFromDB() { }

        /// <summary>
        /// Binds the loader a loaded object reads its <c>[LinkedObject]</c> properties through
        /// the first time each is read: the stack that loaded it. Nucleus calls this once per
        /// loaded object of a type with linked objects; application code does not call it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void BindLinkedObjectLoader(ILinkedObjectLoader loader) { }

        /// <summary>
        /// Takes the value the server stored for a property during a write, such as a value it
        /// computed. It replaces any value the entity holds or has pending for the property.
        /// </summary>
        public void AcceptServerValue(string property, object? value) => BindValue(property, value);
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
