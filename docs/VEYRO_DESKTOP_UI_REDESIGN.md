# VEYRO Desktop — auditoria e arquitetura da experiência

## Auditoria da interface anterior

### Crítico

- A ação principal de envio competia com controles de rede, permissões, mídia, apresentações, pastas e caneta em uma única rolagem.
- O destino do envio ficava visualmente distante do botão de arquivos, aumentando o risco de erro e a carga cognitiva.
- Não existia uma central de transferências: o usuário recebia apenas mensagens textuais sem progresso, velocidade ou histórico da sessão.
- Drag and drop não era tratado como fluxo principal.

### Alto

- A navegação lateral era apenas decorativa e não separava contextos.
- Descoberta, conexão, pareamento e sessão segura não tinham uma hierarquia clara entre estado global e detalhe técnico.
- Empty states eram áreas vazias sem orientação para resolver o problema.
- Solicitações de confiança e permissão usavam diálogos genéricos do Windows, sem contexto visual do VEYRO.
- A interface não possuía tema escuro real nem tokens semânticos.

### Médio

- Quase todas as seções usavam o mesmo card branco, sem níveis claros de superfície.
- A tipografia, os raios, os estados de foco e as categorias de botão não estavam normalizados.
- Recursos avançados apareciam sempre expostos, mesmo quando o usuário só queria enviar um arquivo.
- A janela compacta preservava uma sidebar larga e desperdiçava área útil.

### Baixo

- Textos técnicos como época de grupo e IDs ocupavam o fluxo principal.
- Feedback de sucesso e erro aparecia em pontos diferentes e podia passar despercebido.

## Nova arquitetura

1. **Por perto** — destinos disponíveis, envio, drop zone, descoberta e ações rápidas.
2. **Transferências** — atividades, concluídas e falhas com progresso, rota, velocidade e tempo restante.
3. **Dispositivos** — confiança, permissões, pastas locais/remotas e mesa digitalizadora.
4. **Ajustes** — inicialização, aparência, identidade, transportes e diagnóstico progressivo.

A Home coloca a relação `este computador → fluxo direto → destino` no centro. Informações técnicas continuam disponíveis, mas saem do caminho da tarefa principal.

## Design system

- **Cores:** canvas, surface, raised, muted, text-primary, text-secondary, border, primary, flow, connected, warning e error.
- **Espaçamento:** base de 4 px, com ritmos principais de 8, 12, 16, 20, 24, 32 e 40 px.
- **Raios:** 9 px em controles, 12 px em superfícies internas, 16–18 px em regiões principais.
- **Tipografia:** Segoe UI Variable; títulos de página 28 px, seções 17 px, corpo 13 px e metadados 11 px.
- **Elevação:** duas sombras discretas, usadas apenas em feedback flutuante e diálogos.
- **Motion:** pulsos curtos de descoberta e fluxo; desativados quando o Windows pede animações reduzidas.
- **Componentes:** primary/secondary/ghost/destructive button, nav item, card, inset card, device item, transfer item, empty state, toast, dialog, input, combo e switch.

## Estados e feedback

- Descoberta sem resultados orienta Bluetooth e presença do outro dispositivo.
- Destino desconectado bloqueia o envio com mensagem acionável.
- Drag and drop destaca toda a área e explicita o destino antes de soltar.
- Transferências usam eventos reais do serviço: preparação, aceite, progresso, conclusão, falha e cancelamento.
- Erros incluem o arquivo afetado e a mensagem técnica disponível.
- Solicitações recebidas usam um diálogo contextual com remetente, ação, segurança e escolha explícita.
- Feedback transitório aparece em toast; diagnóstico persistente continua na área de Ajustes.

## Acessibilidade e responsividade

- Navegação por teclado com foco visível; `Ctrl+1…4` alterna áreas e `Ctrl+O` abre o seletor de arquivos.
- A janela compacta reduz a sidebar a ícones e redistribui o conteúdo sem remover a ação principal.
- Estados combinam texto, ícone e forma, não apenas cor.
- Temas Claro, Escuro e Sistema têm superfícies e contrastes próprios.
- Animações respeitam a preferência de redução de movimento do Windows.

## Limites intencionais

- Pausa e retomada individual não são simuladas: exigem suporte correspondente no protocolo de transferência.
- Envio de pastas inteiras continua separado de pastas compartilhadas; o drop rejeita pastas com orientação clara.
- Protocolos BLE, Wi-Fi Direct, criptografia, confiança e roteamento não foram alterados pelo redesign.
