FROM mono:6.12

RUN apt-get update && \
    apt-get install -y curl tar sqlite3 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /opensim
RUN curl -s http://dist.opensimulator.org/opensim-0.9.2.2.tar.gz | tar xzf - --strip-components=1

WORKDIR /opensim/bin
EXPOSE 9000/tcp
EXPOSE 9000/udp

CMD ["mono", "OpenSim.exe"]
