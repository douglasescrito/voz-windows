# Changelog — VozWindows

Todas as alterações relevantes do **VozWindows** são documentadas neste arquivo e publicadas nas Releases do GitHub (`douglasescrito/voz-windows`).

---

## [v1.3.11] — 26/10/2026

Card dedicado com Sliders de Velocidade/Sensibilidade Espacial do Mouse RT (1.2x padrao + presets 0.8x/1.2x/1.8x + Trava do Touch) e modo Ouvir o Computador (WASAPI Loopback ON/OFF)

---

## [v1.3.10] — 26/10/2026

- Reducao e calibragem fina da sensibilidade espacial do Mouse RT (desacoplado do ganho alto do 2o monitor e sem jitter de caixa do FaceDetector)
- Prioridade total ao Touchpad por 260ms + rampa progressiva suave de 350ms + zona morta central auto-adaptativa para combinar facilmente Touch + Rosto

---

## [v1.3.9] — 26/10/2026

- Calibracao espacial hibrida combinando Touchpad + Head Realtime (ajustar com o touch reposiciona a ancora do RT e recalibra o centro espacial do rosto automaticamente)
- Mapeamento espacial direto 1:1 sem drift continuo, com estabilizacao de mira e ratchet automatico nas bordas da tela

---

## [v1.3.8] — 26/10/2026

- Modo Funcao Realtime para controlar o mouse pelo rosto (X e Y continuo em 120 Hz com zona morta central e aceleracao progressiva)
- Botao 'Mouse RT: OFF / ON' na barra, no menu da bandeja e na tela de Configuracao
- Sempre inicia desativado por seguranca (HeadRealtimeMouseEnabled = false na abertura)

---

## [v1.3.7] — 26/10/2026

- **Tela de Teste ao Vivo e Configuração do Head Motion (🎯 Config):** Visor espelhado da webcam em tempo real com marcação do rosto e nariz, medidor visual de giro (Yaw) com marcador de limiar e botões de recalibração e teste imediato dos monitores.
- **Maior Sensibilidade e Modo Longa Distância HD:** Sensibilidade padrão ampliada (2.4x + limiar

---

## [v1.3.6] — 26/10/2026

- **Novo Modo Head Motion Monitor (👀 Head ON / OFF):** Move o mouse em tempo real para o centro do segundo monitor ao virar o rosto para a direita, retornando ao centro do monitor principal quando o rosto volta a ficar reto (com auto-calibração, filtro EMA e histerese para não brigar com o mouse físico).
- **Flag na Barra de Controle e no Menu da Bandeja:** Botão rápido 👀 Head OFF / ON na barra inferior e item correspondente no menu do ícone da bandeja do sistema.

---

## [v1.3.5] — 25/10/2026

- 🟢 **Teletransporte de borda (Loop ON) ativo por padrão ao abrir:** sempre inicia com a repetição de borda (Loop ON) habilitada automaticamente ao abrir o aplicativo.

---

## [v1.3.4] — 25/10/2026

- 📌 **Abertura discreta apenas na bandeja (System Tray):** ao abrir o aplicativo, ele inicia com o microfone desativado (ícone azul) e sem abrir a barra lateral na tela.

---

## [v1.3.3] — 24/10/2026

- 🚫 **Sem inicialização automática com o Windows:** removida a gravação automática no registro de inicialização (Run), abrindo o aplicativo somente quando o usuário desejar.

---

## [v1.3.2] — 20/10/2026

- ⚡ **Modo Nativo Direto (Flag Só Nativo vs. 6 Opções):** opção para usar apenas a transcrição nativa direta no cursor, sem correção e sem gerar as 6 opções de escrita.
- 🧹 **Remoção do completador de palavras (Próxima Palavra):** retirado o bloco de sugestões de palavras para eliminar processamento extra a cada sílaba.
- 🚀 **Otimização profunda de performance em tempo real:** substituição do modelo que causava timeout de 9,5s pelo gemini-2.5-flash (~800ms), eliminação de chamadas de IA durante a fala parcial, preservação contínua do buffer de áudio WebSocket e cache de janela ativa Win32.

---

## [v1.3.1] — 18/10/2026

- 🔀 **Loop e Velocidade independentes:** separação entre o botão de repetição de borda (Loop ON/OFF) e o seletor de velocidade (1.0x, 1.8x, 2.8x, 4.5x).
- 📦 **Automação de Release:** incremento automático de versão e publicação contínua a cada modificação.

---

## [v1.3.0] — 18/10/2026

- 🔀 **Desacoplamento entre Loop e Velocidade (local):** controles independentes na barra inferior e na bandeja para **Repetição de Borda (`🟢 Loop ON` / `⚪ Loop OFF`)** e **Velocidade do Cursor (`⚡ 1.8x` / `⚡ 2.8x` / `⚡ 4.5x` / `⚪ 1.0x`)**.
- 🔔 **Contador de atualizações acumuladas (`1`, `2`, `3`...):** a bolinha verde no ícone perto do relógio e o botão verde na barra lateral agora mostram quantas versões pendentes estão acumuladas.
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
