# This is the home repo of the yaat (Yet another apim tool) dotnet tool

This dotnet global tool aims to help you import/update your Azure API Management APIs from a Swagger specification that can be retrieved from the API's backend service. This tool is supposed to be used in a CI/CD pipeline scenario.

This tool uses the Management SDK to interact with the Azure API Management service, authentication is done using the `DefaultAzureCredential` excluding VisualStudio and VisualStudio code ones (since they won't be installed on the runner machine).

## Getting started

1. Deploy a new version of the service exposed by the API that needs to be updated
2. Install the tool using the following command:

```bash
dotnet tool install MaxDon.ApimUpdater -g
```
3. Run the tool using the following command:

```bash
 yaat --api-name MyAwesomeApi --svc https://my-awesome-service.azurewebsites.net \
               --spec-url https://my-awesome-service.azurewebsites.net/swagger/v1/swagger.json --spec-format openapi-link
```

### Prerequisites

This tool is written in dotnet 7 so the correct version of the runtime should be available on the runner.

> Please note that at the moment the tool doesn't support creating a new API in apim, so the api should already be there.

## Usage

Available command line options:

```
MaxDon.ApimUpdater 1.0.0
Copyright (C) 2023 Massimiliano Donini

  --api-name       Required. The name of the api in the API Management service.

  --svc            Required. The url of the downstream service that will be exposed via the API.

  --spec-url       Required. The url of the downstream service specification e.g. the swagger file.

  --spec-format    Required. The type of the specification, possible values are (openapi, openapi+json, openapi+json-link, openapi-link, swagger-json, swagger-link-json, wadl-link-json, wadl-xml, wsdl and wsdl-link).

  --apim-name      The name of the API Management service, if not provided it will pick the only one present in the selected subscription, or throw if more than one are found.

  --sub-id         The id of the subscription, if not provided it will use the default subscription of the logged in session.

  --sub-name       The Name of the subscription, used to find the most suitable subscription if more than one are found. This has no effect is sub-id is specified.

  --retry          The number of times to retry, max allowed valus is 10.

  --debug          Enables detailed logging.

  --help           Display this help screen.

  --version        Display version information.

```

## Samples

### GitHub Actions reusable workflow with OIDC login and App Service

```yaml
on:
  workflow_call:
    inputs:
      api-name:
        required: true
        type: string    
      site-name:
        required: true
        type: string
    secrets:
      CLIENT_ID:
        required: true
      SUBSCRIPTION_ID:
        required: true
      TENANT_ID:
        required: true

jobs:
  deploy-site:
    runs-on: ubuntu-latest
    timeout-minutes: 30
    permissions:
      id-token: write
      contents: read
    env:
      sleep-time: 30

    steps:
      - name: Download artifact
        uses: actions/download-artifact@v3
        with:
          name: artifact

      - name: Az CLI login
        uses: azure/login@v1
        with:
          client-id: ${{ secrets.CLIENT_ID }}
          tenant-id: ${{ secrets.TENANT_ID }}
          subscription-id: ${{ secrets.SUBSCRIPTION_ID }}

      - name: Run Azure webapp deploy
        uses: azure/webapps-deploy@v2
        with:
          app-name: ${{ inputs.site-name }}
          package: release.zip

      - name: Install Yaat tool
        run: |
          dotnet tool install MaxDon.ApimUpdater -g
          echo Waiting ${{ env.sleep-time }} sec to give time to the web app to re-start
          sleep ${{ env.sleep-time }}

      - name: Update API Management API for ${{ inputs.site-name }}
        run: |
          yaat --api-name ${{ inputs.api-name }} --svc https://${{ inputs.site-name }}.azurewebsites.net \
               --spec-url https://${{ inputs.site-name }}.azurewebsites.net/swagger/v1/swagger.json --spec-format openapi-link

      - name: Logout
        run: az logout
```

## Feedback

- Feel free to open an issue if you find a bug or have a feature request.