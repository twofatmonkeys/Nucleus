global using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public static class ContextGen
    {
        public static IDataContext CreateContext() => CreateContext(false, false, null);

        public static IDataContext CreateContext(IDataContext? parent) => CreateContext(false, false, parent);

        public static IDataContext CreateContext(bool usetransaction) => CreateContext(usetransaction, false, null);

        public static IDataContext CreateContext(bool usetransaction, IDataContext? parent) => CreateContext(usetransaction, false, parent);

        public static IDataContext CreateContext(bool usetransaction, bool usedependency) => CreateContext(usetransaction, usedependency, null);

        public static IDataContext CreateContext(bool usetransaction, bool usedependency, IDataContext? parent)
        {
            throw new NotImplementedException();
        }

        public static Task<IDataContext> CreateContextAsync() => CreateContextAsync(false, false, null, CancellationToken.None);

        public static Task<IDataContext> CreateContextAsync(CancellationToken cancellationToken) => CreateContextAsync(false, false, null, cancellationToken);

        public static Task<IDataContext> CreateContextAsync(IDataContext? parent) => CreateContextAsync(false, false, parent, CancellationToken.None);

        public static Task<IDataContext> CreateContextAsync(IDataContext? parent, CancellationToken cancellationToken) => CreateContextAsync(false, false, parent, cancellationToken);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction) => CreateContextAsync(usetransaction, false, null, CancellationToken.None);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, CancellationToken cancellationToken) => CreateContextAsync(usetransaction, false, null, cancellationToken);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, IDataContext? parent) => CreateContextAsync(usetransaction, false, parent, CancellationToken.None);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, IDataContext? parent, CancellationToken cancellationToken) => CreateContextAsync(usetransaction, false, parent, cancellationToken);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, bool usedependency) => CreateContextAsync(usetransaction, usedependency, null, CancellationToken.None);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, bool usedependency, CancellationToken cancellationToken) => CreateContextAsync(usetransaction, usedependency, null, cancellationToken);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, bool usedependency, IDataContext? parent) => CreateContextAsync(usetransaction, usedependency, parent, CancellationToken.None);

        public static Task<IDataContext> CreateContextAsync(bool usetransaction, bool usedependency, IDataContext? parent, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }


    }
}
