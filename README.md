# VozWindows

Aplicativo nativo Windows (`.NET 8 / WPF`) para ditado por voz ao vivo e reescrita inteligente em 6 formatos operacionais e técnicos.

## Download da Versão Portátil (Pronta para Uso)

- **Última versão (`VozWindows.exe`):** [Baixar última Release no GitHub](https://github.com/douglasescrito/voz-windows/releases/latest/download/VozWindows.exe)
- **Página de Releases:** [github.com/douglasescrito/voz-windows/releases](https://github.com/douglasescrito/voz-windows/releases)

## 6 Opções de Reescrita ao Vivo

1. **Técnica e Detalhada** — Especificação clara, completa e estruturada.
2. **Didática e Simples** — Explicação passo a passo em linguagem acessível.
3. **Operacional / Entrega** — Formato direto de entrega (`O que foi feito:` em voz ativa).
4. **Formal e Institucional** — Comunicação corporativa e polida.
5. **Passo a Passo / Checklist** — Sequência numerada de ações práticas (`1, 2, 3...`).
6. **Direta e Limpa** — Apenas pontuação e correção gramatical da fala original.

## Auto-Update Integrado (1 Clique)

- Ao abrir o aplicativo (e a cada 30 minutos em segundo plano), o `VozWindows` consulta a API pública de Releases deste repositório (`/repos/douglasescrito/voz-windows/releases/latest`).
- Quando uma versão superior à instalada é publicada, o botão **`⬆️ Nova atualização disponível (vX.Y.Z) — Clique para atualizar`** aparece automaticamente no topo da barra lateral.
- Ao clicar no botão, o aplicativo baixa o novo `VozWindows.exe`, substitui o executável atual a quente (`File.Move` para `.exe.old`) e reinicia atualizado.

## Como Publicar uma Nova Versão

```powershell
python publicar-release.py --version 1.2.0
```
