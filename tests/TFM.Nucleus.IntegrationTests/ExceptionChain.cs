namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>Looks through an exception's inner-exception chain.</summary>
    public static class ExceptionChain
    {
        /// <summary>The first exception of type <typeparamref name="T"/> in the chain, starting with <paramref name="ex"/>.</summary>
        public static T? Find<T>(Exception ex) where T : Exception
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is T found)
                    return found;
            return null;
        }
    }
}
