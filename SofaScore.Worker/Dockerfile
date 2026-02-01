# Estágio de Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# INSTALAÇÃO DE DEPENDÊNCIAS
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
    dumb-init \ 
    && rm -rf /var/lib/apt/lists/*

# Variáveis de ambiente para Puppeteer
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true \
    PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_gcServer=0

# Copia o build publicado
COPY --from=build /app/publish .

# Usar dumb-init para melhor gerenciamento de processos
ENTRYPOINT ["dumb-init", "--"]
CMD ["dotnet", "SofaScore.Worker.dll"]