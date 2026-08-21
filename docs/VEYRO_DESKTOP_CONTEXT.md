# Veyro Desktop — Contexto de Continuidade

Este documento preserva as decisões de produto e arquitetura confirmadas para iniciar o desenvolvimento do Veyro Desktop em outra conversa do Codex. Ele deve ser lido junto com o código atual; quando houver divergência, o comportamento implementado e os testes do repositório são a referência técnica, enquanto os requisitos abaixo representam a direção desejada.

## 1. Estado atual do projeto

- Aplicativo Android: `com.veyro.p2p`.
- Nome público: Veyro.
- Versão publicada mais recente: `0.1.8-alpha`.
- Repositório: `https://github.com/Laginh0/Veyro`.
- Branch principal: `main`.
- Commit do release 0.1.8: `3740755`.
- O Android usa Kotlin, Jetpack Compose, Nearby Connections e Protobuf.
- O protocolo e a lógica atuais estão dentro do módulo Android e deverão ser reorganizados gradualmente para compartilhar contratos com o desktop.

Funcionalidades Android já implementadas incluem conexão P2P, Trust Hub, modos de energia, arquivos, bateria, conectividade, ping, notificações, mídia, telefonia/SMS, links, comandos seguros, entrada remota, contatos, apresentação, mesa digitalizadora, pasta compartilhada e clipboard controlado.

A partir da 0.1.8, permissões privilegiadas são opcionais e solicitadas no contexto da função correspondente. Se um acesso obrigatório for negado ou revogado, somente o módulo afetado deve permanecer desativado.

## 2. Objetivo do Veyro Desktop

Criar inicialmente um aplicativo para notebooks Windows que participe do mesmo ecossistema Veyro como um dispositivo completo, e não apenas como destino de arquivos.

O notebook deve poder:

- descobrir, parear e confiar em celulares e outros dispositivos Veyro;
- enviar e receber informações;
- funcionar como coordenador temporário de um grupo;
- manter várias sessões simultâneas;
- encaminhar tráfego entre aparelhos quando necessário;
- continuar operando sem internet, roteador, hotspot previamente configurado ou rede Wi-Fi local existente.

## 3. Requisito central: independência de rede

O Veyro Desktop **não deve depender de uma rede Wi-Fi local**. Não usar a LAN existente como requisito ou como caminho principal.

Requisitos fechados:

- nenhum servidor em nuvem para descoberta ou transporte;
- nenhuma dependência de internet;
- nenhum roteador ou ponto de acesso preexistente;
- dispositivos não precisam estar associados à mesma rede;
- comunicação deve ser criada diretamente pelos rádios dos próprios aparelhos;
- Bluetooth e Wi-Fi precisam estar ligados, mas não conectados a uma infraestrutura externa.

O Quick Share pode servir como referência de produto e arquitetura, mas o Veyro não deve depender do aplicativo, executável ou protocolo privado do Quick Share.

## 4. Transportes planejados

### 4.1 BLE — descoberta e plano de controle

Usar Bluetooth Low Energy para:

- anúncios de presença Veyro;
- descoberta de aparelhos próximos;
- identificação efêmera e anúncio de capacidades;
- início do pareamento;
- negociação do transporte de dados;
- presença de baixa energia;
- recuperação após queda do canal rápido;
- comandos mínimos de controle quando apropriado.

BLE não deve transportar arquivos grandes. Todo conteúdo BLE sensível exige segurança própria na camada do Veyro.

### 4.2 Wi-Fi Direct — canal principal de dados

Usar Wi-Fi Direct para criar uma rede privada temporária entre os dispositivos, sem infraestrutura externa.

O canal deve transportar:

- mensagens Protobuf;
- arquivos e fluxos maiores;
- clipboard;
- notificações;
- mídia;
- comandos;
- estados periódicos;
- tráfego encaminhado entre membros do grupo.

Depois da negociação Wi-Fi Direct, abrir sockets seguros sobre o enlace. O desenho exato do framing, controle de fluxo e multiplexação deve ser documentado antes da implementação definitiva.

### 4.3 Fallbacks

- BLE permanece como plano de controle e recuperação.
- Bluetooth Classic pode ser avaliado como canal intermediário opcional.
- Arquivos grandes devem aguardar o retorno do Wi-Fi Direct quando nenhum transporte adequado estiver disponível.
- Não introduzir fallback obrigatório por rede local.

## 5. Topologia e múltiplos dispositivos

O requisito mínimo é suportar **três aparelhos simultaneamente**, com possibilidade de ampliar conforme o hardware permitir.

A topologia física pode ser uma estrela coordenada temporariamente:

```text
              Notebook / coordenador
               /        |        \
          Celular A  Tablet B  Celular C
```

O comportamento lógico deve ser todos-para-todos:

```text
A <-> B
A <-> C
B <-> C
```

O coordenador serve para organizar o grupo e encaminhar pacotes quando não houver enlace direto. Ele não é um servidor central permanente.

Se o coordenador sair:

1. BLE detecta a perda;
2. os membros elegem outro coordenador;
3. o grupo Wi-Fi Direct é reconstruído;
4. sessões lógicas são retomadas sem duplicação;
5. operações não confirmadas são recuperadas ou canceladas de forma determinística.

A eleição de coordenador deve considerar capacidade do dispositivo, energia, estabilidade, papel atual e suporte do adaptador. Notebook ligado à energia pode ter preferência, sem se tornar obrigatório.

## 6. Roteamento lógico

Cada envelope de rede deve incluir, no mínimo:

- versão do protocolo;
- ID imutável da mensagem;
- ID da origem;
- destinatário específico, conjunto de destinatários ou broadcast autorizado;
- tipo do payload;
- timestamp/validade;
- limite de encaminhamento;
- dados de sequência e confirmação quando necessários;
- assinatura/autenticação da origem;
- conteúdo cifrado para os destinatários.

O roteador precisa impedir:

- loops;
- duplicatas;
- replay;
- encaminhamento após expiração;
- broadcast não autorizado;
- leitura do conteúdo pelo coordenador quando ele não for destinatário.

Transferências e ações devem ser direcionáveis a um aparelho, vários aparelhos selecionados ou todos os membros autorizados.

## 7. Segurança e Trust Hub

Manter os princípios do Android:

- pareamento com PIN igual exibido nos dois dispositivos;
- confirmação explícita em ambos os lados;
- identidade persistente por dispositivo;
- Trust Hub com regras individuais;
- estar no grupo não concede automaticamente acesso a funcionalidades;
- permissões de recurso e regras de confiança continuam separadas por aparelho.

Requisitos adicionais:

- gerar uma chave de identidade por instalação;
- usar troca de chaves autenticada durante o pareamento;
- usar TLS ou protocolo criptográfico equivalente sobre os sockets;
- preferir criptografia ponta a ponta por par ou grupo autorizado;
- guardar chaves com Android Keystore e proteção equivalente no Windows;
- permitir revogar um dispositivo e invalidar sessões futuras;
- nunca transportar chaves privadas pelo coordenador;
- documentar ameaças de intermediário, replay, dispositivo comprometido e coordenador malicioso.

Não inventar primitivas criptográficas. Selecionar bibliotecas e algoritmos consolidados depois de uma revisão técnica específica.

## 8. Protocolo compartilhado

Reutilizar o Protobuf existente, mas separar contrato de transporte e aplicação.

Estrutura atual após a separação dos clientes:

```text
Veyro/
├── mobile/              # Android, documentação e artefatos móveis
├── desktop/             # Windows e documentação desktop
└── protocol/            # .proto e contratos compartilhados
```

O protocolo deve evoluir com compatibilidade explícita:

- negociação de versão;
- anúncio de capacidades;
- recursos opcionais;
- nós que não entendem uma mensagem devem rejeitá-la com segurança;
- 0.1.x Android existente não deve entrar em loop nem aceitar mensagens incompatíveis.

## 9. Plataforma e tecnologia desktop

Plataforma inicial: Windows 10/11 de 64 bits. Windows 11 ARM pode ser avaliado posteriormente.

Direção recomendada:

- C#/.NET para integração confiável com APIs nativas do Windows;
- APIs WinRT para BLE e Wi-Fi Direct;
- UI desktop coerente com o Veyro, sem copiar o Quick Share ou KDE Connect;
- aplicativo na bandeja do sistema;
- início automático opcional;
- serviço/processo em segundo plano compatível com as restrições do Windows;
- notificações nativas;
- instalador reproduzível;
- assinatura oficial do instalador fica para quando houver certificado fornecido pelo proprietário.

A escolha final entre WinUI 3, WPF ou outra camada deve ser feita após um protótipo das APIs BLE/Wi-Fi Direct. Priorizar estabilidade dos transportes e integração do sistema sobre compartilhamento máximo de UI.

## 10. Funcionalidades por ordem sugerida

### Marco 1 — fundação

- criar `desktop/`;
- extrair/compartilhar `.proto`;
- identidade local;
- logs sanitizados;
- interface mínima e bandeja;
- testes unitários do framing e protocolo.

### Marco 2 — descoberta e pareamento

- anúncio e busca BLE no Windows e Android;
- capacidades;
- PIN bilateral;
- Trust Hub desktop;
- reconexão de aparelho conhecido.

### Marco 3 — canal rápido

- grupo Wi-Fi Direct;
- socket seguro;
- keepalive;
- queda e retomada;
- teste sem roteador e sem internet.

### Marco 4 — três aparelhos

- sessões simultâneas;
- endereçamento lógico;
- roteamento todos-para-todos;
- deduplicação;
- eleição e troca de coordenador.

### Marco 5 — primeiras funções úteis

- arquivos em ambas as direções;
- clipboard manual/controlado;
- links;
- bateria, conectividade e ping;
- seleção de destino individual ou múltiplo.

### Marco 6 — integração do sistema

- notificações;
- controle de mídia;
- comandos seguros;
- modo de apresentação;
- permissões opcionais e contextuais no Windows.

### Marco 7 — controle e continuidade

- mouse e teclado;
- mesa digitalizadora;
- pastas selecionadas;
- recuperação prolongada;
- execução em segundo plano;
- instalador alpha.

Não deixar um marco em estado parcialmente integrado: concluir protocolo, UI mínima, testes e diagnóstico antes de avançar.

## 11. Testes obrigatórios

O desenvolvimento exige hardware real. Automatização cobre protocolo e estado, mas não substitui BLE/Wi-Fi Direct físico.

Matriz mínima:

- notebook Windows + um Android;
- notebook Windows + dois Androids;
- três aparelhos conectados simultaneamente;
- telas ligadas/desligadas;
- sem internet;
- sem associação a rede Wi-Fi;
- queda e retorno de Bluetooth;
- queda e reconstrução do Wi-Fi Direct;
- coordenador encerrado;
- firewall bloqueando e depois permitindo o aplicativo;
- permissões negadas e revogadas;
- transferências simultâneas;
- mensagens direcionadas e broadcast;
- hardware/drivers diferentes quando disponíveis.

Registrar latência, tempo de descoberta, tempo de formação do grupo, reconexão, throughput, uso de memória, CPU e consumo aproximado de energia.

Nunca salvar códigos de depuração sem fio, chaves, clipboard real, SMS, contatos ou notificações pessoais nos logs ou no Git.

## 12. Decisões ainda abertas

Não assumir respostas sem protótipo e medição:

- camada de UI Windows (WinUI 3, WPF ou alternativa);
- biblioteca TLS/Noise e esquema exato de identidade;
- framing/multiplexação sobre socket;
- suporte confiável a Bluetooth Classic;
- algoritmo de eleição do coordenador;
- limites de membros por adaptador;
- comportamento em drivers sem recursos Wi-Fi Direct necessários;
- política de fila para arquivos grandes;
- formato de atualização automática;
- compatibilidade futura com Linux e macOS.

Nenhuma dessas questões muda o requisito central de operação direta e independente de infraestrutura.

## 13. Restrições de trabalho e publicação

- Preservar alterações do usuário e evitar operações destrutivas no Git.
- Usar commits pequenos e marcos completos.
- Não publicar releases, tags ou artefatos no GitHub sem pedido explícito do usuário.
- Documentos de testes podem permanecer apenas locais quando solicitado.
- Não prometer compatibilidade universal antes dos testes de hardware.
- Alterações Android necessárias para o desktop devem manter o aplicativo móvel funcional isoladamente.

## 14. Prompt sugerido para a nova conversa

> Leia completamente `desktop/docs/VEYRO_DESKTOP_CONTEXT.md`, inspecione o projeto e o protocolo Android atuais e desenvolva o Veyro Desktop começando pelo Marco 1. Preserve o requisito de BLE + Wi-Fi Direct sem dependência de internet, roteador ou rede local. Não publique no GitHub até eu pedir. Conclua cada marco com testes antes de avançar.

## 15. Continuidade após o teste Android 16 de 2026-08-21

- O Android `0.1.10-alpha` migrou a identidade Keystore para o alias `veyro.desktop.identity.p256.v2` e autoriza `DIGEST_SHA256` e `DIGEST_NONE`. O segundo digest é necessário porque o Conscrypt entrega ao Keystore o hash TLS já calculado e solicita a operação ECDSA bruta. A identidade v1 é removida após a criação bem-sucedida da v2, portanto a atualização exige um novo pareamento.
- BLE foi validado fisicamente nos dois sentidos de descoberta. O caminho Desktop iniciando o GATT é o mais confiável no hardware atual; o Android iniciando contra o GATT do Windows apresentou timeout de status 147.
- O pareamento bilateral e a revogação dos dois Trust Hubs foram validados fisicamente.
- Builds `DEBUG` agora confirmam o PIN automaticamente nos dois aplicativos para acelerar os testes. O bypass do Desktop está protegido por `#if DEBUG`; no Android está protegido por `BuildConfig.DEBUG`. Não remover essas guardas e nunca aplicar o bypass a Release.
- O Android passou a recuperar grupos travados removendo e recriando somente o grupo Wi-Fi Direct. O código não pode desligar, negar ou desconectar a rede Wi-Fi de infraestrutura.
- Um grupo anterior chegou a formar com Windows GO em `192.168.137.1` e Android cliente em `192.168.137.247`, coexistindo com a rede Wi-Fi normal do telefone. No teste mais recente, o adaptador virtual do Windows permaneceu com um grupo órfão e o Android não recriou sua interface P2P; a recuperação automática adicionada precisa ser retestada.
- O novo grupo foi formado com o Wi-Fi comum mantido ativo, usando Android `192.168.137.91`, e a sessão mTLS chegou ao estado ativo no Desktop. O erro `INCOMPATIBLE_DIGEST` não reapareceu com a chave v2.
- Em uma observação anterior, a sessão caiu antes de 45 segundos e o Desktop informou `Canal rápido interrompido; retomada disponível por cinco minutos`. O diagnóstico de keepalive e retomada faz parte da matriz de validação de hardware.
- Publicação: o repositório GitHub deve receber exclusivamente o conteúdo rastreado dentro de `mobile/`, reposicionado na raiz. Não publicar `desktop/`, `protocol/`, este documento ou outros arquivos externos. O Desktop permanece somente local.

## 16. Implementação local do Marco 4

O Marco 4 foi implementado no Desktop após o estado descrito na seção anterior, compilado sem avisos e publicado como `0.1.2-alpha`.

- O listener do canal rápido aceita conexões sucessivas e mantém uma sessão segura independente por dispositivo confiável.
- `TransportEnvelope` ganhou uso efetivo no Desktop com endereçamento individual, múltiplos destinos e broadcast autorizado.
- Todo envelope originado no Desktop recebe assinatura ECDSA da identidade persistente; envelopes recebidos são validados contra o Trust Hub.
- O roteador aplica validade, limite de saltos, deduplicação limitada em memória, entrega local e encaminhamento pelo coordenador.
- O estado de grupo possui lista de membros, época e coordenador. O proprietário inicial do grupo Wi-Fi Direct é adotado como coordenador lógico.
- A eleição é determinística e considera elegibilidade, alimentação externa, capacidade de pares, bateria, estabilidade e desempate por ID.
- Quando o coordenador some, uma nova época é criada e, se este Desktop vencer, ele publica `CoordinatorCommitted` aos membros restantes.
- A interface mostra quantidade de membros, coordenador e época atuais.

A matriz de validação está descrita em `desktop/docs/VEYRO_DESKTOP_MILESTONE_4.md`, incluindo o cenário notebook + dois Androids. A diretriz de publicação mais recente do proprietário é manter o repositório separado `veyro-desktop` somente com o conteúdo de `desktop/`; nada fora dessa pasta pode fazer parte daquele repositório.
