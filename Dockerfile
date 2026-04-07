FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["HermesProductParserFunc.csproj", "./"]
RUN dotnet restore "HermesProductParserFunc.csproj"

COPY . .
RUN dotnet publish "HermesProductParserFunc.csproj" -c Release -o /home/site/wwwroot

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS runtime

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    FUNCTIONS_WORKER_RUNTIME=dotnet-isolated \
    CHROME_BIN=/usr/bin/google-chrome \
    SCRAPE_RUN_LOG_DIR=/home/data/scrape-runs

RUN apt-get update \
    && apt-get install -y --no-install-recommends wget gnupg ca-certificates unzip fonts-liberation libasound2 libatk-bridge2.0-0 libatk1.0-0 libc6 libcairo2 libcups2 libdbus-1-3 libdrm2 libexpat1 libfontconfig1 libgbm1 libgcc-s1 libglib2.0-0 libgtk-3-0 libnspr4 libnss3 libpango-1.0-0 libpangocairo-1.0-0 libstdc++6 libu2f-udev libvulkan1 libx11-6 libx11-xcb1 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 xdg-utils \
    && wget -q -O - https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /usr/share/keyrings/google-linux.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-linux.gpg] http://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends google-chrome-stable \
    && mkdir -p /home/data/scrape-runs /tmp/.cache/selenium \
    && chmod -R 777 /home/data /tmp/.cache \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /home/site/wwwroot /home/site/wwwroot

EXPOSE 80