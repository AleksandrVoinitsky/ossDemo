# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["global.json", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["src/OssDemo.Web/OssDemo.Web.csproj", "src/OssDemo.Web/"]
RUN dotnet restore "src/OssDemo.Web/OssDemo.Web.csproj"

COPY . .
RUN dotnet publish "src/OssDemo.Web/OssDemo.Web.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:RagifyModelDirectory=/src/.ragify-model \
    /p:UseAppHost=false

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "OssDemo.Web.dll"]
