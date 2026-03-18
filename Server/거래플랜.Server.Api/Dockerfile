FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 18080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Server/거래플랜.Server.Api/거래플랜.Server.Api.csproj", "Server/거래플랜.Server.Api/"]
COPY ["Shared/거래플랜.Shared.Contracts/거래플랜.Shared.Contracts.csproj", "Shared/거래플랜.Shared.Contracts/"]
RUN dotnet restore "Server/거래플랜.Server.Api/거래플랜.Server.Api.csproj"
COPY . .
WORKDIR "/src/Server/거래플랜.Server.Api"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "거래플랜.Server.Api.dll"]
