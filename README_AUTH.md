# Admin Authentication Module

This module implements a production-ready, highly secure **Admin Authentication Module** integrated into the `Apenir` .NET 10 solution. It follows the principles of Clean Architecture, Domain-Driven Design (DDD), CQRS (using MediatR), and MongoDB persistence.

---

## Key Features

1. **Security Features:**
   - **JWT Access Tokens:** Fully signed using symmetric HS256 keys.
   - **Refresh Token Rotation:** Rotating refresh tokens upon each use to prevent replay attacks.
   - **Replay Attack Protection:** If a revoked refresh token is re-submitted, all refresh tokens for that administrator are instantly invalidated.
   - **Secure Token Generation:** Cryptographically secure 64-byte random values.
   - **Password Security:** Safe hashing utilizing **BCrypt.Net-Next** (work factor 12) with constant-time verification.
   - **Auditing & IP Tracking:** Every refresh token stores the client IP, User Agent, and device context.
   - **Security stamp/Soft Delete checks:** Deleting or deactivating accounts blocks token rotation and active logins.

2. **API & Middlewares:**
   - **Scalar UI Integration:** Integrated at `/scalar/v1` with native JWT Bearer authentication.
   - **Global Exception Handling:** Standardized error response envelopes matching the result pattern.
   - **Correlation ID Tracking:** Adds and propagates `X-Correlation-ID` across headers and logger context.
   - **Request Logging:** Logs endpoints, methods, execution speeds, and responses safely (without logging secrets/passwords).
   - **Security Headers:** Injects headers like HSTS, X-Frame-Options, X-Content-Type-Options, CSP, and XSS blockages.

---

## Module Architecture

The module is partitioned cleanly across the solution projects:

* **Apenir.Core:** 
  - Domain Entities: `Admin.cs`, `RefreshToken.cs`
  - Domain Exceptions: `Exceptions.cs` (e.g., `InvalidCredentialsException`, `AccountDisabledException`)
* **Apenir.Application:** 
  - Repository interfaces: `IAdminRepository.cs`, `IRefreshTokenRepository.cs`
  - DTO schemas: `LoginRequest`, `LoginResponse`, `RefreshTokenRequest`, `RefreshTokenResponse`, `CurrentAdminResponse`, etc.
  - CQRS Commands/Queries & Handlers (MediatR): `AdminLoginCommand`, `RefreshTokenCommand`, `LogoutCommand`, `GetCurrentAdminQuery`, `ValidateTokenQuery`, etc.
  - Behaviors: `ValidationBehavior<TRequest, TResponse>` running FluentValidation pipelines.
  - Validators: `LoginRequestValidator`, `ChangePasswordRequestValidator`, `ResetPasswordRequestValidator`.
* **Apenir.Infrastructure:**
  - `MongoDbContext`: Connects to MongoDB, manages collection registrations, unique indexes, and a MongoDB TTL index on the token's `ExpiresAt` field.
  - Repositories: `AdminRepository.cs`, `RefreshTokenRepository.cs`
  - Security implementations: `BCryptPasswordHasher.cs`, `JwtTokenService.cs`, `CurrentUserService.cs`
  - Database Seeder: `DatabaseSeeder.cs` (seeds default administrator configuration).
* **Apenir.API:**
  - `AdminAuthController.cs`: Standardized REST endpoints returning `IActionResult` with OpenAPI/Scalar summaries.
  - Middlewares: `CorrelationIdMiddleware`, `RequestLoggingMiddleware`, `SecurityHeadersMiddleware`, `GlobalExceptionHandlingMiddleware`.

---

## Configuration (`appsettings.json`)

Configure your settings in the API project's `appsettings.json` file:

```json
{
  "MongoSettings": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "ApenirDb"
  },
  "JwtSettings": {
    "Secret": "MySuperSecretJWTKey12345678901234567890!@#$%",
    "Issuer": "ApenirAPI",
    "Audience": "ApenirAdmin",
    "AccessTokenExpiryMinutes": 15,
    "RefreshTokenExpiryDays": 7,
    "ClockSkewSeconds": 0
  },
  "AdminSettings": {
    "DefaultEmail": "admin@apenir.com",
    "DefaultUsername": "admin",
    "DefaultPassword": "Admin@Pass123",
    "DefaultFullName": "Super Admin"
  }
}
```

---

## API Endpoints

| Method | Endpoint | Authorization | Description |
| :--- | :--- | :--- | :--- |
| **POST** | `/api/adminauth/login` | Anonymous | Authenticates credentials; returns JWT and Refresh tokens |
| **POST** | `/api/adminauth/refresh` | Anonymous | Rotates refresh token to obtain a new access/refresh pair |
| **POST** | `/api/adminauth/logout` | Anonymous | Revokes the current refresh token |
| **POST** | `/api/adminauth/logout-all` | Authorized | Revokes all refresh tokens issued to the administrator |
| **POST** | `/api/adminauth/change-password` | Authorized | Updates password; validates criteria & matches current password |
| **POST** | `/api/adminauth/forgot-password` | Anonymous | Password reset link trigger (stubbed) |
| **POST** | `/api/adminauth/reset-password` | Anonymous | Password reset finisher using reset token (stubbed) |
| **GET** | `/api/adminauth/me` | Authorized | Fetches profile of the currently logged in administrator |
| **GET** | `/api/adminauth/validate-token` | Anonymous | Queries validity status of a given JWT token |

---

## Run and Verify

1. **Build and Run the API:**
   ```bash
   cd src/Apenir.API
   dotnet run
   ```
2. **Access Documentation:**
   - OpenAPI specification: `http://localhost:5000/openapi/v1.json` (or configured HTTPS port)
   - Interactive Scalar Reference UI: `http://localhost:5000/scalar/v1`
3. **Execute Test Suites:**
   ```bash
   cd src
   dotnet build
   dotnet test --no-build
   ```
