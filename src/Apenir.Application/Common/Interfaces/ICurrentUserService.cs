using System;

namespace Apenir.Application.Common.Interfaces
{
    public interface ICurrentUserService
    {
        Guid? UserId { get; }
        string? Email { get; }
        string? IpAddress { get; }
        string? UserAgent { get; }
    }
}
