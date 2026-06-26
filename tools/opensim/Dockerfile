FROM ubuntu:22.04

RUN apt-get update && \
    DEBIAN_FRONTEND=noninteractive apt-get install -y mono-complete curl tar sqlite3 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /opensim
RUN curl -s http://dist.opensimulator.org/opensim-0.9.2.2.tar.gz | tar xzf - --strip-components=1

WORKDIR /opensim/bin
EXPOSE 9000/tcp
EXPOSE 9000/udp

CMD ["mono", "OpenSim.exe"]
