# Changelog — VozWindows

Todas as alterações relevantes do **VozWindows** são documentadas neste arquivo e publicadas nas Releases do GitHub (`douglasescrito/voz-windows`).

---

## [v1.3.0] — 17/10/2026

- 🔔 **Contador de atualizações acumuladas (1, 2, 3...):** a bolinha verde no ícone perto do relógio e o botão verde na barra lateral agora mostram quantas versões pendentes estão acumuladas.
- 📋 **Resumo consolidado de todas as versões:** ao clicar para atualizar, exibe todas as novidades das versões que você pulou e instala direto a mais recente de uma só vez.

---

## [v1.2.1] — 17/10/2026

- ✅ **Teste ao vivo de Atualização Automática (v1.2.1):** se este aviso apareceu na sua barra lateral e a bolinha verde acendeu no ícone perto do relógio, o Auto-Update está funcionando!
- ⚡ **Verificação acelerada:** checagem automática de novas versões a cada 2 minutos em segundo plano.
- 🎯 **Como concluir o teste:** clique em **Sim** abaixo para o aplicativo baixar a v1.2.1 sozinho, reiniciar atualizado e confirmar que o topo mudou para **v1.2.1**!

---

## [v1.2.0] — 17/10/2026

- **Bolinha verde de notificação de atualização na bandeja:** quando há uma nova versão disponível no GitHub, o ícone ao lado do relógio exibe uma bolinha verde esmeralda (`#22C55E`) com aro branco no canto superior direito.
- **Ícone dinâmico de estado do microfone na bandeja:** o ícone na bandeja do sistema fica **Vermelho** (`#DC2626`) enquanto o microfone está ligado/ouvindo (`F8`) e **Azul** (`#2563EB`) quando o microfone está desligado.
- **Janela explícita de aceitação (`Sim` / `Não`):** o aplicativo nunca atualiza sozinho; ao clicar no banner verde de atualização, exibe as novidades da versão e aguarda a confirmação do usuário antes de baixar e reiniciar.
- **Modo de pré-visualização local:** opção `👁️ Pré-visualizar Aviso de Atualização (Simulação)` no menu da bandeja para testar o comportamento visual a qualquer momento.

---

## [v1.1.0] — 17/10/2026

- **Auto-Update integrado via GitHub Releases:** verificação automática de novas versões em segundo plano e troca a quente do executável (`File.Move` para `.exe.old`).
- **6 novas opções de reescrita ao vivo (foco técnico e operacional):**
  1. Técnica e Detalhada
  2. Didática e Simples
  3. Operacional / Entrega (`O que foi feito:`)
  4. Formal e Institucional
  5. Passo a Passo / Checklist (`1, 2, 3...`)
  6. Direta e Limpa
- **Versão portátil dedicada à voz (`PORTABLE_VOICE_ONLY`):** estabilidade de cursor e docagem AppBar para distribuição em executável único.
