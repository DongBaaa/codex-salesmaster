FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Server/SalesMaster.Server.Api/SalesMaster.Server.Api.csproj", "Server/SalesMaster.Server.Api/"]
COPY ["Shared/SalesMaster.Shared.Contracts/SalesMaster.Shared.Contracts.csproj", "Shared/SalesMaster.Shared.Contracts/"]
RUN dotnet restore "Server/SalesMaster.Server.Api/SalesMaster.Server.Api.csproj"
COPY . .
WORKDIR "/src/Server/SalesMaster.Server.Api"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SalesMaster.Server.Api.dll"]
