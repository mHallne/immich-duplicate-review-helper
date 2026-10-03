FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ImmichDuplicateReview.slnx ./
COPY src/ImmichDuplicateReview/ImmichDuplicateReview.csproj src/ImmichDuplicateReview/
RUN dotnet restore src/ImmichDuplicateReview/ImmichDuplicateReview.csproj
COPY src/ImmichDuplicateReview/ src/ImmichDuplicateReview/
RUN dotnet publish src/ImmichDuplicateReview/ImmichDuplicateReview.csproj -c Release -o /app --no-restore
RUN mkdir -p /container-data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID /container-data /data
ENV ASPNETCORE_URLS=http://+:8080 DATA_PATH=/data
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ImmichDuplicateReview.dll"]
