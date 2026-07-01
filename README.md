# PomodoroWidget

Cliente desktop (Windows, C#/.NET Framework 4.7.2 + WinForms + WebView2) para o [sistemadetarefas.online](https://sistemadetarefas.online).

## O que é

Um widget de Pomodoro que roda no seu Windows e se conecta à sua conta em sistemadetarefas.online para sincronizar tarefas e sessões de estudo.

> **Nota:** este repositório contém apenas o código do **cliente**. O backend (API/servidor) não é open source.

## Como instalar (usuário final)

Baixe o executável mais recente na aba [Releases](../../releases) — não é necessário compilar nada.

## Como compilar (desenvolvedores)

Pré-requisitos:
- Visual Studio 2022+ com carga de trabalho **.NET desktop development**
- .NET Framework 4.7.2 Developer Pack

Passos:
1. Clone o repositório
2. Abra `PomodoroWidget.sln` no Visual Studio
3. Restaure os pacotes NuGet (`Microsoft.Web.WebView2`, `Newtonsoft.Json`) — acontece automaticamente ao compilar
4. Compile em modo `Release`

Para gerar um executável standalone (sem depender de runtime instalado):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

## Autenticação

O app usa OAuth 2.0 Authorization Code + PKCE com redirecionamento via loopback local (`http://127.0.0.1:<porta>/callback/`), sem nenhum client secret embutido no cliente — seguindo o padrão recomendado para aplicativos nativos/desktop ([RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252)).

## Contribuindo

Pull requests são bem-vindos. Para mudanças maiores, abra uma issue primeiro para discutirmos a abordagem.

## Licença

Este projeto está licenciado sob a licença MIT — veja o arquivo [LICENSE](LICENSE) para detalhes.
