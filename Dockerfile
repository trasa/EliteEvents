FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY EliteEvents.sln ./
COPY version.json ./
COPY Directory.Build.props ./
COPY dotnet-tools.json ./

COPY EliteEvents.Eddn/EliteEvents.Eddn.csproj EliteEvents.Eddn/
COPY EliteEvents.Visitors/EliteEvents.Visitors.csproj EliteEvents.Visitors/

RUN dotnet restore EliteEvents.Visitors/EliteEvents.Visitors.csproj

COPY EliteEvents.Eddn/ EliteEvents.Eddn/
COPY EliteEvents.Visitors/ EliteEvents.Visitors/

WORKDIR /src/EliteEvents.Visitors
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EliteEvents.Visitors.dll"]
