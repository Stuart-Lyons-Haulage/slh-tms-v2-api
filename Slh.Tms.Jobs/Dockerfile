FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Slh.Tms.Api.csproj ./
COPY Slh.Tms.Jobs/Slh.Tms.Jobs.csproj Slh.Tms.Jobs/
RUN dotnet restore Slh.Tms.Jobs/Slh.Tms.Jobs.csproj
COPY . .
RUN dotnet publish Slh.Tms.Jobs/Slh.Tms.Jobs.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Slh.Tms.Jobs.dll"]
