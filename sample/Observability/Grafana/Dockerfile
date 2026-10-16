FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json nuget.config version.json ./

FROM source AS publish-api
COPY Trellis.ServiceLevelIndicators/src/Trellis.ServiceLevelIndicators.csproj Trellis.ServiceLevelIndicators/src/
COPY Trellis.ServiceLevelIndicators.Asp/src/Trellis.ServiceLevelIndicators.Asp.csproj Trellis.ServiceLevelIndicators.Asp/src/
COPY sample/SliProductionDemo/SliProductionDemo.csproj sample/SliProductionDemo/
RUN dotnet restore sample/SliProductionDemo/SliProductionDemo.csproj
COPY . .
RUN dotnet publish sample/SliProductionDemo/SliProductionDemo.csproj \
    --configuration Release --no-restore --output /out/api -p:UseAppHost=false

FROM source AS publish-traffic
COPY sample/GenerateSli/GenerateSli.csproj sample/GenerateSli/
RUN dotnet restore sample/GenerateSli/GenerateSli.csproj
COPY . .
RUN dotnet publish sample/GenerateSli/GenerateSli.csproj \
    --configuration Release --no-restore --output /out/traffic -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=publish-api /out/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SliProductionDemo.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS traffic
WORKDIR /app
COPY --from=publish-traffic /out/traffic .
USER $APP_UID
ENTRYPOINT ["dotnet", "GenerateSli.dll"]
