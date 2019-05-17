using System;

namespace DotNetOutdated.Exceptions
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
    }
}