using System;

namespace TFM.Nucleus
{
    /// <summary>
    /// The base of every exception Nucleus defines, so a caller can catch what Nucleus throws
    /// as a family: <c>catch (NucleusException)</c>. Each derived type says what failed. Nucleus
    /// also throws the framework's own exceptions where they are the right ones
    /// (<see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
    /// <see cref="InvalidOperationException"/>, <see cref="OperationCanceledException"/>); those
    /// are not wrapped.
    /// </summary>
    public abstract class NucleusException : Exception
    {
        protected NucleusException()
        {
        }

        protected NucleusException(string? message) : base(message)
        {
        }

        protected NucleusException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
