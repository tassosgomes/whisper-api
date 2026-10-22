# Ícone do Whisper Recorder

Microfone geométrico branco com ponto de gravação coral, sobre uma base azul de
cantos arredondados. Sem texto, gradientes ou detalhes finos; margem transparente
e base sólida para uso nos temas claro e escuro.

Cores: azul `#2463D4`, branco `#FFFFFF` e coral `#FF8A80`.

Para regenerar, na raiz do repositório, com Python 3 e Pillow 12.x já disponíveis:

```bash
python3 clients/windows-recorder/assets/icon/generate_icon.py
```

O gerador escreve `icon.svg` (vetor editável), `icon-256.png`, `icon-1024.png` e
`icon.ico`, com PNGs RGBA de 16, 24, 32, 48, 64, 128 e 256 px embutidos. Cada
tamanho usa supersampling e redução Lanczos. As formas e cores do gerador também
produzem o SVG; edite essas constantes para manter as alterações ao regenerar.

Arte e gerador originais deste projeto, sujeitos à mesma licença do repositório.
Este checkout não declara uma licença própria; estes arquivos não atribuem uma
licença adicional nem incorporam marcas ou arte de terceiros.
