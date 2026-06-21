# MediLab Backend Architecture (scaffold)

This document describes the high-level architecture scaffold created for the Apenir backend (.NET 10).

Folders created:
- `src/Apenir.API` - Web API project
- `src/Apenir.Core` - Core domain entities and interfaces
- `src/Apenir.Application` - Application services and business logic
- `src/Apenir.Infrastructure` - Database, caching, external integrations
- `src/Apenir.Shared` - Shared helpers and extensions
- `tests` - Unit and integration test projects

Next steps:
- Run `dotnet sln add` for each csproj and adjust package versions as needed.
- Implement controllers, services, and repositories per the project spec.
