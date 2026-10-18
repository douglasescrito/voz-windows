# Instrucoes do projeto VozWindows

## Regra obrigatoria ao concluir qualquer modificacao

Toda alteracao funcional, visual ou de correcao no `VozWindows` **deve obrigatoriamente** terminar com a publicacao de uma nova Release no GitHub para liberar a atualizacao automaticamente as pessoas que ja baixaram o aplicativo:

1. Compilar e reiniciar a instancia local em `bin\Release\net8.0-windows\VozWindows.exe`.
2. Executar o publicador oficial (que incrementa a versao automaticamente quando `--version` nao e informado, atualiza o `CHANGELOG.md`, faz commit/push na branch `master` + tag `vX.Y.Z` e envia o novo `bin\Portatil\VozWindows.exe` para o GitHub Releases):

```powershell
python publicar-release.py --notes "- Descricao clara do que mudou nesta atualizacao."
```

## Separacao entre ambiente local (PROJETOS) e executavel portatil (Clientes)

- **Execucao local (`PROJETOS`):** mantem ativos os controles independentes de **Loop (`🟢 Loop ON` / `⚪ Loop OFF`)** e **Velocidade (`⚡ 1.8x` / `⚡ 2.8x` / `⚡ 4.5x` / `⚪ 1.0x`)**.
- **Executavel portatil (`bin\Portatil\VozWindows.exe` / GitHub Releases):** compilado automaticamente por `publicar-release.py` com `-p:DefineConstants=PORTABLE_VOICE_ONLY` e chave ofuscada em memoria (restaurando `NativeInterop.cs` imediatamente em `finally:`), sem nunca gravar segredos no codigo-fonte ou no Git.
