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
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /src/.ragify-model/onnx \
    && curl --fail --location --retry 3 --output /src/.ragify-model/onnx/model_O1.onnx https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/e8f8c211226b894fcb81acc59f3b34ba3efd5f42/onnx/model_O1.onnx \
    && curl --fail --location --retry 3 --output /src/.ragify-model/tokenizer.json https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/e8f8c211226b894fcb81acc59f3b34ba3efd5f42/tokenizer.json \
    && test "$(sha256sum /src/.ragify-model/onnx/model_O1.onnx | awk '{print $1}')" = "9ae4b831e992807334f18a91557661e94715f502a5c7248fb81675b08391e30f" \
    && test "$(sha256sum /src/.ragify-model/tokenizer.json | awk '{print $1}')" = "2c3387be76557bd40970cec13153b3bbf80407865484b209e655e5e4729076b8"
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
