# 1. Base Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

# 2. SDK Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy all .csproj files first to cache dependencies efficiently
COPY ["src/Apenir.API/Apenir.API.csproj", "src/Apenir.API/"]
COPY ["src/Apenir.Application/Apenir.Application.csproj", "src/Apenir.Application/"]
COPY ["src/Apenir.Core/Apenir.Core.csproj", "src/Apenir.Core/"]
COPY ["src/Apenir.Infrastructure/Apenir.Infrastructure.csproj", "src/Apenir.Infrastructure/"]

# Restore the main API entry project dependencies
RUN dotnet restore "src/Apenir.API/Apenir.API.csproj"

# Copy the rest of your application code over
COPY . .

# Compile the solution inside the API folder
WORKDIR "/src/src/Apenir.API"
RUN dotnet build "Apenir.API.csproj" -c Release -o /app/build

# 3. Publish Stage
FROM build AS publish
RUN dotnet publish "Apenir.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 4. Final Production Ready Stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Apenir.API.dll"]