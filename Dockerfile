FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props Wedding.sln ./
COPY src/ src/
RUN dotnet restore src/Wedding.Api/Wedding.Api.csproj
RUN dotnet publish src/Wedding.Api/Wedding.Api.csproj --no-restore -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
CMD ["dotnet", "Wedding.Api.dll"]
