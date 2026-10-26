# ============================================================================
#  Kyoshin Dashboard server image — built ON TrueNAS from already-published files
#
#  Build context: /mnt/MyCloud/Yolo/KyoshinDashboard
#     Dockerfile
#     app/        <- dotnet publish -c Release -r linux-x64 --self-contained false
#  Build:   docker build -t kyoshin-dashboard:<version> .
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
# --chmod: files copied over SMB can be rwx--x--- — make them readable for the non-root user
COPY --chmod=755 app/ ./

# Run as TrueNAS "apps" (UID/GID 568): datasets with the Apps ACL preset already grant it access,
# so /data (Kyoshin.db) needs no extra permissions.
RUN mkdir -p /data && chown 568:568 /data

ENV TZ=Asia/Ho_Chi_Minh \
    HOME=/data \
    ASPNETCORE_ENVIRONMENT=Production \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 \
    ConnectionStrings__KyoshinDb="Data Source=/data/Kyoshin.db"

USER 568:568
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "KyoshinDashboard.dll"]
