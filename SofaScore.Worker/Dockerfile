# Estágio de Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia os projetos (assumindo contexto na raiz do repo)
COPY ["SofaScore.Shared/SofaScore.Shared.csproj", "SofaScore.Shared/"]
COPY ["SofaScore.Worker/SofaScore.Worker.csproj", "SofaScore.Worker/"]

# Restaura dependências
RUN dotnet restore "SofaScore.Worker/SofaScore.Worker.csproj"

# Copia todo o código fonte
COPY . .

# Publica o Worker
WORKDIR "/src/SofaScore.Worker"
RUN dotnet publish -c Release -o /app/publish

# Estágio de Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

###########################################################
# INSTALAÇÃO DE DEPENDÊNCIAS DO CHROME (Essencial para Linux)
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
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

# Variáveis de ambiente para o Puppeteer usar o Chromium instalado
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true \
    PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_gcServer=0

# Copia o build publicado
COPY --from=build /app/publish .

# Define o ponto de entrada
ENTRYPOINT ["dotnet", "SofaScore.Worker.dll"]