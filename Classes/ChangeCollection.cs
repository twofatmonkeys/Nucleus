using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public class ChangeCollection : IEnumerable<Change>
    {
        private readonly List<Change> Changes = [];

        public bool Contains<T>(Expression<Func<T>> expr)
        {
            var property = "";
            if (expr.Body is MemberExpression mexpr)
                if (mexpr.Member != null)
                    property = mexpr.Member.Name;

            var l = Changes.Where(x => x.Property == property);
            if (l.Any())
                return true;
            else
                return false;
        }
        public int Count => Changes.Count;
        public bool HasChanges { get { return Count > 0; } }
        public void Add(Change changes)
        {
            Changes.Add(changes);
        }
        public void Add(string property, object oldvalue, object newvalue, bool supress, bool abbreviate)
        {
            if (Changes.Where(x => x.Property == property).Any())
                Changes.Single(x => x.Property == property).SetNewValue(newvalue);
            else
                Changes.Add(new Change(property, oldvalue, newvalue, supress, abbreviate));
        }
        public void Clear()
        {
            Changes.RemoveRange(0, Changes.Count);
        }

        public override string ToString()
        {
            if (Changes.Count > 0)
            {
                string s = "";
                int i = 0;
                foreach (Change c in Changes)
                {
                    if (!c.Suppress)
                    {
                        if (c.Abbreviate)
                        {
                            if (!string.IsNullOrEmpty(c.NewValue))
                            {
                                if (i > 0)
                                    s += ", u";
                                else
                                    s += "U";
                                s += $"pdated {c.Property} to a new value";
                                i++;
                            }
                            else
                            {
                                if (i > 0)
                                    s += ", c";
                                else
                                    s += "C";
                                s += "leared the value from " + c.Property;
                                i++;
                            }
                        }
                        else if (string.IsNullOrEmpty(c.OldValue) && string.IsNullOrEmpty(c.NewValue))
                        {
                            if (i > 0)
                                s += ", o";
                            else
                                s += "O";
                            s += "verwrote " + c.Property;
                            i++;
                        }
                        else if (string.IsNullOrEmpty(c.OldValue))
                        {
                            if (i > 0)
                                s += ", s";
                            else
                                s += "S";
                            s += "et " + c.Property + " to '" + c.NewValue + "'";
                            i++;
                        }
                        else if (string.IsNullOrEmpty(c.NewValue))
                        {
                            if (i > 0)
                                s += ", c";
                            else
                                s += "C";
                            s += "leared '" + c.OldValue + "' from " + c.Property;
                            i++;
                        }
                        else
                        {
                            if (i > 0)
                                s += ", u";
                            else
                                s += "U";
                            s += "pdated " + c.Property + " from '" + c.OldValue + "' to '" + c.NewValue + "'";
                            i++;
                        }
                    }
                }
                return s;
            }
            else
                return string.Empty;
        }

        public IEnumerator<Change> GetEnumerator()
        {
            return Changes.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Changes.GetEnumerator();
        }
    }
}
