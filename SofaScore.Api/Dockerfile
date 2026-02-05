# Use a imagem do SDK para build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia os arquivos de projeto (csproj) e restaura as dependências
# Note que copiamos o Shared também
COPY ["SofaScore.Api/SofaScore.Api.csproj", "SofaScore.Api/"]
COPY ["SofaScore.Shared/SofaScore.Shared.csproj", "SofaScore.Shared/"]

# Restaura as dependências
RUN dotnet restore "SofaScore.Api/SofaScore.Api.csproj"

# Copia todo o resto do código
COPY . .

# Faz o build da API
WORKDIR "/src/SofaScore.Api"
RUN dotnet build "SofaScore.Api.csproj" -c Release -o /app/build

# Publica a aplicação
FROM build AS publish
RUN dotnet publish "SofaScore.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Imagem final para execução
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SofaScore.Api.dll"]