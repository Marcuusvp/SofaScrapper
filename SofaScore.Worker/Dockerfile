FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SofaScore.Worker/SofaScore.Worker.csproj", "SofaScore.Worker/"]
COPY ["SofaScore.Shared/SofaScore.Shared.csproj", "SofaScore.Shared/"]
RUN dotnet restore "SofaScore.Worker/SofaScore.Worker.csproj"
COPY . .
WORKDIR "/src/SofaScore.Worker"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

###########################################################
# INSTALAÇÃO DE DEPENDÊNCIAS DO CHROME (Crítico para Linux)
###########################################################
RUN apt-get update && apt-get install -y \
    chromium \
    libnss3 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    && rm -rf /var/lib/apt/lists/*

# Variáveis de ambiente para o Puppeteer usar o Chromium instalado
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true \
    PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SofaScore.Worker.dll"]