# PomodoroWidget

Cliente desktop (Windows, C#/.NET Framework 4.7.2 + WinForms + WebView2) para o [sistemadetarefas.online](https://sistemadetarefas.online).

## O que é

Um widget de Pomodoro flutuante e sempre visível que roda no seu Windows e se conecta à sua conta em sistemadetarefas.online para sincronizar tarefas e sessões de estudo.

> **Nota:** este repositório contém apenas o código do **cliente**. O backend (API/servidor) não é open source.

## Funcionalidades

- Widget flutuante, sem bordas, sempre no topo
- Login integrado via navegador (OAuth 2.0 + PKCE — veja [Autenticação](#autenticação))
- Ícone na bandeja do sistema (system tray): clique duas vezes para reabrir o widget se ele estiver escondido, ou clique com o botão direito para fechar o aplicativo de vez

## Como instalar (usuário final)

1. Baixe o instalador mais recente (`PomodoroWidget-Setup-x.x.x.exe`) na aba [Releases](../../releases)
2. Execute e siga o assistente — não é necessário compilar nada
3. Após instalado, o widget fica disponível pelo atalho no Menu Iniciar e por um ícone na bandeja do sistema

## Como compilar (desenvolvedores)

Pré-requisitos:
- Visual Studio 2022+ com carga de trabalho **.NET desktop development**
- .NET Framework 4.7.2 Developer Pack
- [Inno Setup](https://jrsoftware.org/isinfo.php) (opcional, só se for gerar o instalador)

Passos:
1. Clone o repositório
2. Abra `PomodoroWidget.sln` no Visual Studio
3. Restaure os pacotes NuGet (`Microsoft.Web.WebView2`, `Newtonsoft.Json`) — acontece automaticamente ao compilar
4. Troque a configuração de **Debug** para **Release** e recompile a solução

> ⚠️ Este é um projeto .NET Framework clássico (`packages.config`), não .NET Core/5+. Não use `dotnet publish`/`dotnet build` — use o MSBuild do Visual Studio (Menu **Compilação → Recompilar Solução**, ou `msbuild PomodoroWidget.sln /t:Rebuild /p:Configuration=Release` num Developer Command Prompt).

O executável final, com todas as DLLs necessárias (WebView2, Newtonsoft.Json, `icon.ico`), fica em `PomodoroWidget\bin\Release\`.

### Gerando o instalador

Com o build em Release pronto, compile `installer.iss` no Inno Setup Compiler (`Ctrl+F9`). O instalador final é gerado em `installer-output\`.

## Autenticação

O app usa OAuth 2.0 Authorization Code + PKCE com redirecionamento via loopback local (`http://127.0.0.1:<porta>/callback/`), sem nenhum client secret embutido no cliente — seguindo o padrão recomendado para aplicativos nativos/desktop ([RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252)).

## Contribuindo

Pull requests são bem-vindos. Para mudanças maiores, abra uma issue primeiro para discutirmos a abordagem.

## Licença

Este projeto está licenciado sob a licença MIT — veja o arquivo [LICENSE](LICENSE) para detalhes.
