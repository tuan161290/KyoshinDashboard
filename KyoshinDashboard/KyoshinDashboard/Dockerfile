# ========================================
# KyoshinDashboard - Multi-stage Docker Build
# ========================================

# --- Build Stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY KyoshinDashboard.csproj .
RUN dotnet restore

# Copy everything and publish
COPY . .
RUN dotnet publish -c Release -o /app/publish

# --- Runtime Stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create data directory for SQLite
RUN mkdir -p /app/data

# Copy published app
COPY --from=build /app/publish .

# Expose port
EXPOSE 8080

ENTRYPOINT ["dotnet", "KyoshinDashboard.dll"]
