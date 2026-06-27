using System;
using System.Collections.Generic;

namespace Apenir.Core.Exceptions
{
    public class InvalidCredentialsException : Exception
    {
        public InvalidCredentialsException(string message = "Invalid email/username or password.") : base(message) { }
    }

    public class AccountDisabledException : Exception
    {
        public AccountDisabledException(string message = "This account has been disabled.") : base(message) { }
    }

    public class TokenExpiredException : Exception
    {
        public TokenExpiredException(string message = "The access token has expired.") : base(message) { }
    }

    public class RefreshTokenExpiredException : Exception
    {
        public RefreshTokenExpiredException(string message = "The refresh token has expired.") : base(message) { }
    }

    public class RefreshTokenRevokedException : Exception
    {
        public RefreshTokenRevokedException(string message = "The refresh token has been revoked.") : base(message) { }
    }

    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message = "Unauthorized access.") : base(message) { }
    }

    public class ValidationException : Exception
    {
        public List<string> Errors { get; } = new();

        public ValidationException(string message = "One or more validation failures occurred.") : base(message) { }

        public ValidationException(List<string> errors, string message = "One or more validation failures occurred.") : base(message)
        {
            Errors = errors;
        }
    }
}
