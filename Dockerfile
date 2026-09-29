# build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/VctHub.Web/VctHub.Web.csproj src/VctHub.Web/
RUN dotnet restore src/VctHub.Web/VctHub.Web.csproj
COPY src/VctHub.Web/ src/VctHub.Web/
RUN dotnet publish src/VctHub.Web/VctHub.Web.csproj -c Release -o /app --no-restore

# run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY seed/ /app/seed/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Seed__Dir=/app/seed
EXPOSE 8080
ENTRYPOINT ["dotnet", "VctHub.Web.dll"]
