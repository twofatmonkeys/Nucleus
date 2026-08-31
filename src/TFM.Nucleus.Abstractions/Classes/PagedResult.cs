using System.Collections.Generic;

namespace TFM.Nucleus
{
    /// <summary>
    /// One page of results plus the unpaged total, fetched in a single round trip
    /// (SELECT + COUNT_BIG(*) batched by the provider).
    /// </summary>
    public class PagedResult<T> : List<T>
    {
        public long TotalCount { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; }

        /// <summary>True when rows beyond this page exist.</summary>
        public bool HasMore => Skip + Count < TotalCount;

        public PagedResult() { }
        public PagedResult(IEnumerable<T> items) : base(items) { }
    }
}
