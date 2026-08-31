namespace TFM.Nucleus.Data
{
    public interface IWhereCollection
    {
        public void AddAnd(IQueryParameter parameter);
        public void AddOr(IQueryParameter parameter);
        public void AddWhereAnd(IWhereCollection whereCollection);
        public void AddWhereOr(IWhereCollection whereCollection);
    }
}
