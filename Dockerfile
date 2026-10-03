FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ImmichDuplicateReview.slnx ./
COPY src/ImmichDuplicateReview/ImmichDuplicateReview.csproj src/ImmichDuplicateReview/
RUN dotnet restore src/ImmichDuplicateReview/ImmichDuplicateReview.csproj
COPY src/ImmichDuplicateReview/ src/ImmichDuplicateReview/
RUN dotnet publish src/ImmichDuplicateReview/ImmichDuplicateReview.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 DATA_PATH=/data
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ImmichDuplicateReview.dll"]
