FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY . .

FROM source AS publish-api
RUN dotnet publish sample/SliProductionDemo/SliProductionDemo.csproj \
    --configuration Release --output /out/api -p:UseAppHost=false

FROM source AS publish-traffic
RUN dotnet publish sample/GenerateSli/GenerateSli.csproj \
    --configuration Release --output /out/traffic -p:UseAppHost=false

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
