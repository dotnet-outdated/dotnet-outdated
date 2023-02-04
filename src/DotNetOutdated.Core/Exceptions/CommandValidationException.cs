using System;

namespace DotNetOutdated.Core.Exceptions
{
    public class CommandValidationException : Exception
    {
        public CommandValidationException(string message)
            : base(message)
        {
        }

        public CommandValidationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public CommandValidationException()
        {
        }
    }
}
